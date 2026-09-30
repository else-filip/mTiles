using System.Runtime.CompilerServices;
using System.Collections.Specialized;
using System.ComponentModel;
using mTiles.Services;
using mTiles.Services.Phone.Remote;

namespace mTiles.ViewModels;

/// <summary>The Goal tile as a paired phone sees it and drives it.</summary>
/// <remarks>
/// Every command goes through the same command the tile's own buttons run, so each rule about phases,
/// pauses and what may be answered when is kept in one place and not restated for a phone.
/// </remarks>
public partial class GoalTileViewModel : IRemoteAttachTile
{
    private long _remoteVersion;

    /// <inheritdoc />
    /// <remarks>Moves with every property the tile raises and every change to the transcript — the gate's
    /// countdown and the run's status line included, since both are properties.</remarks>
    public long RemoteVersion => Interlocked.Read(ref _remoteVersion);

    private void WatchForRemote() =>
        Messages.CollectionChanged += (_, _) => Interlocked.Increment(ref _remoteVersion);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        Interlocked.Increment(ref _remoteVersion);
        base.OnPropertyChanged(e);
    }

    /// <inheritdoc />
    public RemoteTileBody DescribeForRemote()
    {
        var chat = GoalChatProjection.Project(Messages, RemotePendingNow());
        var text = IsRunning && ActivityText.Length > 0 ? ActivityText : StatusText.Length > 0 ? StatusText : null;
        var detail = PhaseLabel.Length > 0 ? PhaseLabel : null;

        return new RemoteTileBody(
            "chat",
            new RemoteStatus(RemoteActivity.Of(Activity), text, detail),
            Chat: chat,
            Composer: new RemoteComposer(
                ShowComposer || ShowApproval || ShowQuestions,
                ShowApproval ? "Type changes to the plan, or approve it"
                : ShowQuestions ? "Answer above, or type here"
                : CurrentPhase is Models.GoalPhase.Goal or Models.GoalPhase.Summary ? "Describe the goal"
                : IsRunning ? "The goal is running" : "Type a message",
                CanInterrupt: IsRunning && !IsPaused,
                Draft: InputText.Length > 0 ? InputText : null,
                TakesAttachments: true,
                Modes: RemoteModes(),
                StartsOver: StartingOverWouldAskHere(),
                SyncsDraft: true),
            NewLabel: "New goal");
    }

    /// <summary>The composer's other sends — the menu beside the desktop's Set goal button, and the two
    /// detect buttons — offered only where a goal is what the tile is waiting for, as they are there.</summary>
    /// <remarks>Which of them wants text and which works without it is said rather than decided here: the
    /// text is the phone's own draft, which this side has not seen.</remarks>
    private IReadOnlyList<RemoteSendMode>? RemoteModes()
    {
        if (!CanSetGoal) return null;
        var canDetect = CanDetectGoal;
        return
        [
            new RemoteSendMode(RunMode, "Set goal & run", NeedsText: true),
            new RemoteSendMode(ReviewMode, "Review against it", NeedsText: true),
            new RemoteSendMode(DetectMode, "Detect goal", NeedsText: false, canDetect),
            new RemoteSendMode(DetectRunMode, "Detect & run", NeedsText: false, canDetect),
            new RemoteSendMode(DetectReviewMode, "Review changes", NeedsText: false, canDetect),
        ];
    }

    private const string RunMode = "run";
    private const string ReviewMode = "review";
    private const string DetectMode = "detect";
    private const string DetectRunMode = "detect-run";
    private const string DetectReviewMode = "detect-review";

    /// <summary>The discard question already answered on a paired phone, for the one command that phone
    /// started, and read by <see cref="ConfirmDiscardAsync"/> in place of the computer's own dialog. It flows
    /// with that command through every await it makes, and never reaches any other command. A box rather
    /// than the flag itself, because the answer is spent where it is read: an <see cref="AsyncLocal{T}"/>
    /// written inside an async method does not reach its caller, while a box both of them hold does.</summary>
    private readonly AsyncLocal<StrongBox<bool>?> _discardAnsweredRemotely = new();

    /// <summary>Starts a command with the discard question already answered on the phone.</summary>
    private void StartAnswered(bool discard, Func<Task> command) =>
        StartWithoutWaiting(RunAnsweredAsync(discard, command));

    private async Task RunAnsweredAsync(bool discard, Func<Task> command)
    {
        _discardAnsweredRemotely.Value = new StrongBox<bool>(discard);
        await command();
    }

    /// <inheritdoc />
    /// <remarks>What it is waiting for first, since that is the reason to go and look; otherwise what the
    /// run is doing, or how it stopped.</remarks>
    public TilePreview? PreviewForRemote()
    {
        var text = ShowQuestions ? QuestionsTitle
            : ShowApproval ? "Plan waiting for approval"
            : ShowReviewGate ? (ReviewGateLine.Length > 0 ? ReviewGateLine : "The review is in")
            : IsRunning && ActivityText.Length > 0 ? ActivityText
            : StatusText.Length > 0 ? StatusText
            : PhaseLabel;
        if (PhaseLabel.Length > 0 && text != PhaseLabel && !ShowQuestions && !ShowApproval)
            text = $"{PhaseLabel} · {text}";

        DateTimeOffset? at = Messages.Count > 0
            ? new DateTimeOffset(DateTime.SpecifyKind(Messages[^1].Timestamp, DateTimeKind.Utc))
            : null;
        return new TilePreview(text, null, at);
    }

    /// <summary>The one block the tile is waiting on, in the order it draws them.</summary>
    private RemotePending? RemotePendingNow()
    {
        if (ShowQuestions)
        {
            return new RemotePending(
                "questions",
                QuestionsPendingId(),
                QuestionsTitle,
                null,
                [],
                [
                    .. Questions.Select(q => new RemoteQuestion(
                        q.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        q.HasWhy ? q.Why : null,
                        q.Question,
                        [.. q.Options.Select(o => o.Text)],
                        Multi: false,
                        Custom: true)),
                ]);
        }

        if (ShowApproval)
        {
            return new RemotePending(
                "plan",
                PlanPendingId(),
                "Approve the plan?",
                _engine.ProposedPlan,
                [new RemoteOption("approve", "Approve plan", "primary")]);
        }

        if (ShowReviewGate)
        {
            return new RemotePending(
                "gate",
                GatePendingId(),
                ReviewGateLine.Length > 0 ? ReviewGateLine : "The review is in.",
                null,
                GateIsCounting
                    ? [new RemoteOption("continue", "Continue now", "primary"), new RemoteOption("pause", "Pause", "neutral")]
                    : [new RemoteOption("resume", "Resume", "primary")],
                SecondsLeft: GateIsCounting ? _gateRemaining : null);
        }

        return null;
    }

    private const string QuestionsPrefix = "clarify:";
    private const string PlanPrefix = "plan:";
    private const string GatePrefix = "gate:";

    /// <summary>Names this review's gate, so a late press meant for an earlier review cannot carry on
    /// past the next one before its findings were read. The transcript's length tells apart two reviews
    /// that happened to find the same things.</summary>
    private string GatePendingId() =>
        GatePrefix + ContentStamp(Messages.Count + "\n" + string.Join('\n', _picking.Select(f => f.Defect)));

    /// <summary>Names this round of questions, so an answer to an earlier round is refused rather than
    /// written under this round's numbers.</summary>
    private string QuestionsPendingId() =>
        QuestionsPrefix + ContentStamp(string.Join('\n', Questions.Select(q => q.Question)));

    /// <summary>Names this plan, so an approval of one the phone was shown cannot approve its successor.</summary>
    private string PlanPendingId() => PlanPrefix + ContentStamp(_engine.ProposedPlan ?? "");

    private static string ContentStamp(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)), 0, 8);

    /// <inheritdoc />
    public Task<RemoteRefusal?> HandleRemoteAsync(RemoteTileCommand command) => Task.FromResult(HandleRemote(command));

    private RemoteRefusal? HandleRemote(RemoteTileCommand command)
    {
        switch (command)
        {
            case RemoteSendText { Mode: { Length: > 0 } mode } send:
                return HandleRemoteMode(mode, send);

            case RemoteSendText send:
                if (send.Text.Trim().Length == 0) return "There is nothing to send.";
                if (IsRunning && !ShowApproval && !ShowQuestions)
                    return "The goal is running. Pause it first, or wait for it to stop.";

                if (send.Submit && !send.Discard && StartingOverWouldAskHere())
                    return StartsOverRefusal;

                if (RemoteText.WouldOverwrite(InputText, send.Text, send.Replaces)) return RemoteText.DraftInTheWay;
                InputText = send.Text;
                if (!send.Submit) return null;

                StartAnswered(send.Discard,
                    () => ShowApproval ? ApproveOrChangeCommand.ExecuteAsync(null) : SubmitCommand.ExecuteAsync(null));
                return null;

            case RemoteDraft typed:
                if (RemoteText.WouldOverwrite(InputText, typed.Text, typed.Seen)) return RemoteText.DraftInTheWay;
                InputText = typed.Text;
                return null;

            case RemoteNewConversation:
                // A run in flight is stopped and waited out by StartNewConversationAsync itself, as on
                // the computer; the phone has already said so when it asked.
                StartAnswered(true, StartNewConversationAsync);
                return null;

            case RemoteChoose { OptionId: "approve" } approve when approve.PendingId.StartsWith(PlanPrefix, StringComparison.Ordinal):
                if (!ShowApproval) return "There is no plan waiting for approval.";
                if (approve.PendingId != PlanPendingId()) return "The plan has changed since the phone showed it.";
                // An empty composer is what approves; a typed change is a draft that exists nowhere else.
                if (!string.IsNullOrWhiteSpace(InputText)) return RemoteText.DraftInTheWay;
                StartWithoutWaiting(ApproveOrChangeCommand.ExecuteAsync(null));
                return null;

            case RemoteChoose gate when gate.PendingId.StartsWith(GatePrefix, StringComparison.Ordinal):
                if (!ShowReviewGate) return "The review is no longer waiting.";
                if (gate.PendingId != GatePendingId()) return "A newer review is waiting now.";
                switch (gate.OptionId)
                {
                    case "continue" when GateIsCounting: ContinueNowCommand.Execute(null); return null;
                    case "pause" when GateIsCounting: PauseAtGateCommand.Execute(null); return null;
                    case "resume" when !GateIsCounting: StartWithoutWaiting(ResumeCommand.ExecuteAsync(null)); return null;
                    default: return "That is not on offer any more.";
                }

            case RemoteAnswer answer when answer.PendingId.StartsWith(QuestionsPrefix, StringComparison.Ordinal):
                if (!ShowQuestions) return "Those questions have already been answered.";
                if (answer.PendingId != QuestionsPendingId()) return "Those questions have been replaced by new ones.";
                var remoteAnswers = RemoteAnswersByQuestion(answer);
                // Checked for every question before any is written, so a refused round leaves nothing half-applied.
                if (remoteAnswers.Any(pair => RemoteText.WouldOverwrite(pair.Question.Answer, pair.Text)))
                    return RemoteText.DraftInTheWay;
                foreach (var (question, text) in remoteAnswers) question.Answer = text;
                StartWithoutWaiting(SendAnswersCommand.ExecuteAsync(null));
                return null;

            case RemoteInterrupt:
                if (!IsRunning || IsPaused) return "The goal is not running.";
                // Not PauseCommand: that asks on this machine's screen, which nobody at the phone can see.
                Pause();
                return null;

            default:
                return "The goal tile cannot do that from a phone.";
        }
    }

    private const string StartsOverRefusal =
        "Starting a new goal discards this one. Reload the page on the phone, or press + on the computer first.";

    /// <summary>One of <see cref="RemoteModes"/>, run through the command the desktop's own control runs.</summary>
    private RemoteRefusal? HandleRemoteMode(string mode, RemoteSendText send)
    {
        if (!CanSetGoal)
            return IsRunning ? "The goal is running. Pause it first, or wait for it to stop." : "That is not on offer right now.";
        if (!send.Discard && StartingOverWouldAskHere()) return StartsOverRefusal;

        var typed = send.Text.Trim().Length > 0;
        var typedGoal = GoalScopeFilter.WordsOnly(send.Text).Length > 0;
        if (mode is RunMode or ReviewMode && !typedGoal) return "Type the goal first.";
        if (mode is DetectMode or DetectRunMode or DetectReviewMode && !typed && !CanDetectGoal)
            return "There are no changes to read a goal from.";

        Func<Task>? command = mode switch
        {
            RunMode => () => SetGoalAndRunCommand.ExecuteAsync(null),
            ReviewMode => () => ReviewCommand.ExecuteAsync(null),
            DetectMode => () => DetectAsync(andRun: false),
            DetectRunMode => () => DetectAsync(andRun: true),
            DetectReviewMode => () => DetectAsync(andRun: false, andReview: true),
            _ => null,
        };
        if (command is null) return "That is not on offer right now.";

        // What the phone typed goes where the desktop's controls read it: for a typed goal it is the goal,
        // for a detect it narrows what is read, exactly as words typed beside those buttons do.
        if (RemoteText.WouldOverwrite(InputText, send.Text, send.Replaces)) return RemoteText.DraftInTheWay;
        InputText = send.Text;

        StartAnswered(send.Discard, command);
        return null;
    }

    /// <inheritdoc />
    public async Task<RemoteAttachResult> AttachFromRemoteAsync(string name, string mimeType, byte[] data)
    {
        if (RemoteAttachmentFile.IsAgentImage(mimeType))
        {
            string path;
            try
            {
                // Decoded and encoded off the UI thread: a photo is hundreds of milliseconds of both.
                path = await Task.Run(() => ImageStore.SavePng(PngImage.From(data)));
            }
            catch (Exception ex)
            {
                return new RemoteAttachResult(null, $"The image could not be saved: {ex.Message}");
            }

            var marker = _engine.AttachImage(path);
            SaveStateSoon();
            return new RemoteAttachResult(marker);
        }

        return await RemoteAttachmentFile.AttachAsync(name, data, _workingDirectory);
    }

    /// <summary>The phone's answer to each question it answered, joined the way the composer shows a choice.</summary>
    private List<(GoalQuestionAnswer Question, string Text)> RemoteAnswersByQuestion(RemoteAnswer answer) =>
        Questions
            .Select(question => (Question: question,
                Given: answer.Answers.GetValueOrDefault(question.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .Where(pair => pair.Given is { Count: > 0 })
            .Select(pair => (pair.Question, string.Join(", ", pair.Given!)))
            .ToList();

    /// <summary>Whether a goal sent now would open the discard question on this machine's screen, which
    /// nobody at the phone can see or answer — the same question <c>SubmitCore</c> asks.</summary>
    private bool StartingOverWouldAskHere() =>
        CurrentPhase is Models.GoalPhase.Goal or Models.GoalPhase.Summary && GoalTilePolicy.WorthConfirming(Messages);

    /// <summary>Answers the phone as soon as a command has started: approving, answering or resuming
    /// runs the whole loop, which outlasts the phone's request timeout by minutes, and a phone told
    /// "no answer" while the run is going invites a second press.</summary>
    private static void StartWithoutWaiting(Task command) =>
        command.ContinueWith(
            t => System.Diagnostics.Trace.TraceError($"[GoalTile] A command from the phone failed: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
}
