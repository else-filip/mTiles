using System.Diagnostics;
using mTiles.AgentSessions;
using mTiles.AgentSessions.Commands;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.AgentSessions.Storage;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Phone.Remote;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>What a paired phone is shown of this tile, and what it may ask of it.</summary>
public sealed partial class AgentConversationTileViewModel
{
    // ── A paired phone ───────────────────────────────────────────────────────────────────────

    /// <summary>Something asked from a phone: a failure is the phone's refusal, never an <c>ok</c> over nothing done.</summary>
    private async Task<string?> AttemptFromRemoteAsync(Func<Task> action, string failed)
    {
        try
        {
            await action();
            return null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return "The tile was closed.";
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[AgentConversation] {failed} (from a phone): {ex}");
            return $"{failed}: {ex.Message}";
        }
    }

    /// <summary>The conversation as last drawn, which is what a phone is shown — the same state the
    /// transcript above was synced from, so the two cannot disagree.</summary>
    private ConversationState _remoteState = ConversationState.Empty;

    private long _remoteVersion;

    /// <inheritdoc />
    /// <remarks>Moves with every property this tile raises, which includes every draw: conservative, and
    /// a counter costs nothing, while missing a change costs a phone showing a stale answer.</remarks>
    public long RemoteVersion => Interlocked.Read(ref _remoteVersion);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        Interlocked.Increment(ref _remoteVersion);
        base.OnPropertyChanged(e);
    }

    /// <inheritdoc />
    public RemoteTileBody DescribeForRemote()
    {
        var chat = AgentChatProjection.Project(_remoteState);
        var status = LaunchProblem ?? (StatusText.Length > 0 ? StatusText : null);
        var canType = CanSend();

        return new RemoteTileBody(
            "chat",
            new RemoteStatus(RemoteActivity.Of(Activity), status, HeaderNote.Length > 0 ? HeaderNote : null,
                ContextPercent),
            Chat: chat,
            Composer: new RemoteComposer(
                canType,
                canType ? "Message the agent" : IsStarting ? "The agent is starting…" : "The agent is not running",
                CanInterrupt: IsBusy && CanInterrupt,
                Draft: Draft.Length > 0 ? Draft : null,
                TakesAttachments: true,
                Pickers: RemotePickers(),
                CanCompact: CanCompactNow(),
                SyncsDraft: true),
            NewLabel: "New conversation");
    }

    /// <inheritdoc />
    public TilePreview? PreviewForRemote() =>
        LaunchProblem is { } problem
            ? new TilePreview(problem)
            : AgentChatProjection.Preview(_remoteState, ContextPercent);

    /// <inheritdoc />
    /// <remarks>Answers are checked against the request the agent is waiting on <em>now</em>: the phone's
    /// id is as old as the last picture it was sent, and an approval answered after it was replaced must
    /// not be read as an answer to the next one.</remarks>
    public async Task<RemoteRefusal?> HandleRemoteAsync(RemoteTileCommand command)
    {
        switch (command)
        {
            case RemoteSendText send:
                if (!CanSend()) return NotReadyRefusal();
                return SendRemoteText(send.Text, send.Submit, send.Replaces);

            case RemoteDraft typed:
                if (RemoteText.WouldOverwrite(Draft, typed.Text, typed.Seen)) return RemoteText.DraftInTheWay;
                Draft = typed.Text;
                return null;

            case RemotePick pick:
                return await PickFromRemoteAsync(pick);

            case RemoteCompact:
                if (!CanCompact) return "This agent cannot compact its context.";
                if (!CanSend()) return NotReadyRefusal();
                if (!CanCompactNow() || _host is not { } compacting)
                    return "The agent is busy. Compact once it has finished.";
                // Asked on the phone; the computer's own question would wait on a screen nobody is looking at.
                return await AttemptFromRemoteAsync(
                    () => compacting.ExecuteAsync(new CompactContext(), _lifetime.Token), "Compacting failed");

            case RemoteOpenConversation open:
                return await OpenFromRemoteAsync(open.ConversationId);

            case RemoteNewConversation:
                // Asked on the phone, over the transcript it is showing; the computer's own question would
                // wait on a screen nobody at the phone can see.
                await StartNewConversationCoreAsync(askFirst: false);
                return null;

            case RemoteKey key:
                return TryPressKey(key.Key) ? null : "That key does nothing here.";

            case RemoteInterrupt:
                if (!IsBusy) return "The agent is not working on anything.";
                await InterruptAsync();
                return null;

            case RemoteChoose choose:
                if (AgentChatProjection.ResolveChoice(_remoteState, choose.PendingId, choose.OptionId, out var refusal)
                    is not var (approval, decision))
                    return refusal;
                await AnswerApprovalAsync(approval.RequestId, decision);
                return null;

            case RemoteAnswer answer:
                if (_remoteState.PendingQuestions.All(q => q.RequestId != answer.PendingId))
                    return "Those questions have already been answered.";
                await AnswerQuestionsAsync(answer.PendingId, answer.Answers);
                return null;

            default:
                return "The agent cannot do that from a phone.";
        }
    }

    /// <summary>The strip's three pickers as a phone offers them: what the session reported, nothing more.</summary>
    private IReadOnlyList<RemotePicker>? RemotePickers()
    {
        var pickers = new List<RemotePicker>();
        if (ModelOptions.Count > 0 || Model.Length > 0)
            pickers.Add(new RemotePicker("model", "Model", Model.Length > 0 ? Model : null, Model.Length > 0 ? Model : null,
                [.. ModelOptions.Select(m => new RemoteChoice(m, m))], Custom: true));
        if (HasModeOptions)
            pickers.Add(new RemotePicker("mode", "Mode", SelectedMode?.Id, SelectedMode?.Label,
                [.. ModeOptions.Select(m => new RemoteChoice(m.Id, m.Label,
                    SessionSettingOptions.ParseMode(m.Id) == AiBehaviour.BypassPermissions ? BypassWarning : null))]));
        if (HasEffortOptions)
            pickers.Add(new RemotePicker("effort", "Effort", SelectedEffort?.Id, SelectedEffort?.Label,
                [.. EffortOptions.Select(e => new RemoteChoice(e.Id, e.Label))]));
        return pickers.Count > 0 ? pickers : null;
    }

    /// <summary>The one sentence for the bypass grant, asked by the computer's strip and by the phone alike.</summary>
    private const string BypassWarning =
        "Run this agent with no permission checks at all?\n\n" +
        "It will edit, create and delete files and run commands in this workspace without asking. " +
        "This applies to this tile, until you change it back.";

    /// <summary>A picker set from the phone — through the same change the strip makes, but without the
    /// computer's bypass question, which the phone has asked already (the choice carries the warning).</summary>
    /// <remarks>Refused up front while a running session is busy: the host refuses such a change with a notice
    /// rather than an error, so going ahead would answer the phone <c>ok</c> over a setting that did not change.</remarks>
    private async Task<string?> PickFromRemoteAsync(RemotePick pick)
    {
        if (_host is { HasSession: true } && IsBusy)
            return "This agent cannot switch while it works. Stop the turn, or change it once the turn is over.";

        switch (pick.Picker)
        {
            case "model":
                var model = (pick.Value ?? string.Empty).Trim();
                if (model.Length == 0 || model == Model) return null;
                return await PickSettingFromRemoteAsync(new SessionSettings(Model: model), () => Model = model);
            case "mode" when ModeOptions.FirstOrDefault(m => m.Id == pick.Value) is { } mode:
                return await PickSettingFromRemoteAsync(new SessionSettings(Mode: mode.Id), () => SelectedMode = mode);
            case "effort" when EffortOptions.FirstOrDefault(e => e.Id == pick.Value) is { } effort:
                return await PickSettingFromRemoteAsync(new SessionSettings(Effort: effort.Id), () => SelectedEffort = effort);
            default:
                return "That is not on offer any more.";
        }
    }

    private async Task<string?> PickSettingFromRemoteAsync(SessionSettings change, Action select)
    {
        var refusal = await AttemptFromRemoteAsync(() => ApplySettingsAsync(change), "Changing the setting failed");
        if (refusal is null) ShowPickedWithoutSession(select);
        return refusal;
    }

    /// <summary>With no session to report it back, a choice kept for the next launch is drawn here, as the strip
    /// draws its own — without handing it to <see cref="ChangeSettingsAsync"/> a second time.</summary>
    private void ShowPickedWithoutSession(Action select)
    {
        if (_host is { HasSession: true }) return;
        _drawingSettings = true;
        try
        {
            select();
        }
        finally
        {
            _drawingSettings = false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteConversation>> ConversationsForRemoteAsync() =>
        (await Conversations.ReadOptionsAsync()).Select(option => new RemoteConversation(
            option.Summary.Id, option.Title, option.Note, option.IsCurrent, option.Reason)).ToList();

    /// <summary>Opens another stored conversation from the phone. Refused while the agent works, rather than
    /// asking on the computer whether to stop it: that question would wait on a screen nobody is looking at.</summary>
    private async Task<string?> OpenFromRemoteAsync(string conversationId)
    {
        if (conversationId == ConversationId) return null;
        if (IsBusy) return "The agent is working. Stop it first, then open the other conversation.";
        ConversationSummary? summary;
        try
        {
            summary = await Conversations.FindStoredAsync(conversationId);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[AgentConversation] Reading the conversations for the phone failed: {ex}");
            return "mTiles could not read the conversations.";
        }

        if (summary is null) return "That conversation is no longer there.";
        if (RefusalFor(summary) is { } refusal) return refusal;
        // Asked again inside the switch: a turn begun meanwhile is refused here rather than asked about on a
        // screen nobody is looking at.
        string? outcome = "The conversation could not be opened.";
        await RunAsync(async () => outcome = await SwitchConversationAsync(summary, () => Task.FromResult(!IsBusy)));
        return outcome;
    }
}
