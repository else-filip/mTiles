using System.Text;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;

namespace mTiles.Services.Phone.Remote;

/// <summary>
/// An Agent tile's conversation as a phone draws it.
/// </summary>
/// <remarks>
/// <para>Pure, and fed the same <see cref="ConversationState"/> the tile draws from, so what the phone
/// shows is the desktop's transcript and not a second reading of the events that could disagree with
/// it.</para>
/// <para>What is left out is left out on purpose. A tool call's output and a diff are what the desktop
/// is for; on a phone a work group is one line per step — what it did and whether it worked — which is
/// the question somebody on the sofa is asking. Reasoning is dropped: it is the one thing in a transcript
/// that is long, streamed, and never acted on.</para>
/// </remarks>
public static class AgentChatProjection
{
    /// <summary>How many timeline entries a phone is sent, newest last.</summary>
    public const int MaxItems = 60;

    /// <summary>The most of one message a phone is sent. A message longer than this is the tool pasting
    /// a file, and the head of it says what it is.</summary>
    public const int MaxTextLength = 16_000;

    public static RemoteChat Project(ConversationState state, int maxItems = MaxItems)
    {
        var timeline = state.Timeline;
        var skip = Math.Max(0, timeline.Count - maxItems);
        var items = new List<RemoteChatItem>(timeline.Count - skip);

        for (var i = skip; i < timeline.Count; i++)
        {
            if (Item(timeline[i]) is { } item)
                items.Add(item);
        }

        return new RemoteChat(items, skip, Pending(state), Plan(state.Plan));
    }

    /// <summary>
    /// The conversation in one line, for the tile's card in the miniature: what it is asking, else the step
    /// it is on, else what was last said — and when that last happened.
    /// </summary>
    /// <remarks>Walks back from the end and stops at the first entry with anything to say, so it costs a
    /// handful of entries however long the conversation is.</remarks>
    public static TilePreview Preview(ConversationState state, double? contextPercent)
    {
        var at = LastMoment(state);

        if (state.PendingApprovals.FirstOrDefault() is { } approval)
            return new TilePreview($"Allow? {approval.Title}", contextPercent, at);
        if (state.PendingQuestions.FirstOrDefault()?.Questions.FirstOrDefault() is { } question)
            return new TilePreview($"Asks: {question.Text}", contextPercent, at);

        for (var i = state.Timeline.Count - 1; i >= 0; i--)
        {
            switch (state.Timeline[i])
            {
                case WorkGroupEntry work when work.Items.OfType<ToolCallItem>().LastOrDefault() is { } call:
                    return new TilePreview(call.Title, contextPercent, at);
                case MessageEntry { Role: MessageRole.User } m when TilePreviews.OneLine(m.Text) is { } said:
                    return new TilePreview($"You: {said}", contextPercent, at);
                case MessageEntry m when TilePreviews.OneLine(m.Text) is { } said:
                    return new TilePreview(said, contextPercent, at);
                case NoticeEntry n when n.Level == NoticeLevel.Error:
                    return new TilePreview(n.Text, contextPercent, at);
            }
        }

        return new TilePreview(null, contextPercent, at);
    }

    /// <summary>When the conversation last moved: its newest entry, or a tool call in it finishing.</summary>
    private static DateTimeOffset? LastMoment(ConversationState state)
    {
        if (state.Timeline.Count == 0) return null;
        var last = state.Timeline[^1];
        var at = last.At;
        if (last is WorkGroupEntry work)
        {
            foreach (var call in work.Items.OfType<ToolCallItem>())
            {
                var moment = call.CompletedAt ?? call.StartedAt;
                if (moment > at) at = moment;
            }
        }
        return at == default ? null : at;
    }

    /// <summary>The first thing the agent is waiting on: an approval before a round of questions, the
    /// order the tile draws them in.</summary>
    public static RemotePending? Pending(ConversationState state)
    {
        if (state.PendingApprovals.FirstOrDefault() is { } approval)
        {
            return new RemotePending(
                "approval",
                approval.RequestId,
                approval.Title,
                Clip(approval.Detail),
                [.. approval.Options.Select(o => new RemoteOption(o.Decision.ToString(), o.Label, ToneOf(o.Decision)))]);
        }

        if (state.PendingQuestions.FirstOrDefault() is { } round)
        {
            return new RemotePending(
                "questions",
                round.RequestId,
                round.Questions.Count == 1 ? "The agent is asking" : $"The agent is asking {round.Questions.Count} questions",
                null,
                [],
                [.. round.Questions.Select(q => new RemoteQuestion(
                    q.Id, q.Header, q.Text, [.. q.Options.Select(o => o.Label)], q.MultiSelect, q.AllowsCustomAnswer))]);
        }

        return null;
    }

    /// <summary>Reads an approval option id back into the decision it was sent for.</summary>
    public static bool TryDecision(string optionId, out ApprovalDecision decision) =>
        Enum.TryParse(optionId, ignoreCase: false, out decision) && Enum.IsDefined(decision);

    /// <summary>Which approval a phone's press answers, and with what — or the sentence saying why it
    /// answers nothing.</summary>
    /// <remarks>Matched by request id against what is pending <em>now</em>, never by position: the phone's
    /// id is as old as the last picture it was sent, and a late press must not answer the next approval.
    /// </remarks>
    public static (ApprovalRequested Approval, ApprovalDecision Decision)? ResolveChoice(
        ConversationState state, string pendingId, string optionId, out string? refusal)
    {
        refusal = null;
        if (state.PendingApprovals.FirstOrDefault(a => a.RequestId == pendingId) is not { } approval)
        {
            refusal = "That question has already been answered.";
            return null;
        }
        if (!TryDecision(optionId, out var decision) || approval.Options.All(o => o.Decision != decision))
        {
            refusal = "That is not one of the answers on offer.";
            return null;
        }
        return (approval, decision);
    }

    private static string ToneOf(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Accept => "primary",
        ApprovalDecision.AcceptForSession => "neutral",
        ApprovalDecision.Cancel => "danger",
        _ => "neutral",
    };

    private static IReadOnlyList<RemotePlanStep>? Plan(PlanUpdated? plan) =>
        plan is null || plan.Steps.Count == 0
            ? null
            : [.. plan.Steps.Select(s => new RemotePlanStep(s.Text, s.Status switch
            {
                PlanStepStatus.Completed => "done",
                PlanStepStatus.InProgress => "running",
                _ => "pending",
            }))];

    private static RemoteChatItem? Item(TimelineEntry entry) => entry switch
    {
        MessageEntry m => new RemoteChatItem(
            m.Id,
            m.Role == MessageRole.User ? "user" : "assistant",
            WithImages(Clip(m.Text) ?? "", m.Images.Count),
            Markdown: true,
            Streaming: m.IsStreaming),

        WorkGroupEntry w => Work(w),

        ProposedPlanEntry p => new RemoteChatItem(p.Id, "plan", Clip(p.Markdown) ?? "", Markdown: true),

        QuestionsEntry q => new RemoteChatItem(q.Id, "questions", Answered(q)),

        CheckpointEntry c when c.Files.Count > 0 => new RemoteChatItem(
            c.Id, "notice", Changed(c), Tone: c.Restored ? "warning" : "info"),

        NoticeEntry n => new RemoteChatItem(n.Id, "notice", n.Text, Tone: n.Level switch
        {
            NoticeLevel.Error => "error",
            NoticeLevel.Warning => "warning",
            _ => "info",
        }),

        HandoverEntry h => new RemoteChatItem(h.Id, "notice",
            $"Handed over to {h.To.InstanceName ?? h.To.AgentId}.", Tone: "info"),

        _ => null,
    };

    private static RemoteChatItem? Work(WorkGroupEntry group)
    {
        var lines = new List<RemoteWorkLine>(group.Items.Count);
        foreach (var item in group.Items)
        {
            switch (item)
            {
                case ToolCallItem call:
                    lines.Add(new RemoteWorkLine(
                        call.SubAgent is { } sub ? $"{call.Title} · {sub.Title}" : call.Title,
                        call.SubAgent is { IsWorking: true } ? "running" : StateOf(call.State)));
                    break;
                case DecisionItem decision:
                    lines.Add(new RemoteWorkLine($"{decision.Title} — {Said(decision.Decision)}",
                        decision.Decision is ApprovalDecision.Accept or ApprovalDecision.AcceptForSession
                            ? "done"
                            : "declined"));
                    break;
            }
        }

        if (lines.Count == 0)
            return null;

        var running = lines.Count(l => l.State == "running");
        var text = running > 0
            ? $"{lines.Count} {Steps(lines.Count)}, {running} running"
            : $"{lines.Count} {Steps(lines.Count)}";
        return new RemoteChatItem(group.Id, "work", text, Work: lines);
    }

    private static string Steps(int count) => count == 1 ? "step" : "steps";

    private static string StateOf(ToolCallState state) => state switch
    {
        ToolCallState.Running => "running",
        ToolCallState.Completed => "done",
        ToolCallState.Declined => "declined",
        _ => "failed",
    };

    private static string Said(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Accept => "allowed once",
        ApprovalDecision.AcceptForSession => "allowed for the session",
        ApprovalDecision.Decline => "declined",
        _ => "stopped",
    };

    private static string Answered(QuestionsEntry round)
    {
        var sb = new StringBuilder();
        foreach (var question in round.Questions)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(question.Text);
            if (round.Answers?.TryGetValue(question.Id, out var answer) == true && answer.Count > 0)
                sb.Append("\n→ ").Append(string.Join(", ", answer));
            else
                sb.Append("\n→ (no answer)");
        }
        return sb.ToString();
    }

    private static string Changed(CheckpointEntry checkpoint)
    {
        var files = checkpoint.Files.Count;
        var added = checkpoint.Files.Sum(f => f.Additions);
        var removed = checkpoint.Files.Sum(f => f.Deletions);
        var what = $"Changed {files} {(files == 1 ? "file" : "files")} (+{added} −{removed})";
        return checkpoint.Restored ? what + " — undone" : what;
    }

    private static string WithImages(string text, int images) => images switch
    {
        0 => text,
        1 => text.Length == 0 ? "[image]" : $"{text}\n\n[image]",
        _ => text.Length == 0 ? $"[{images} images]" : $"{text}\n\n[{images} images]",
    };

    internal static string? Clip(string? text) =>
        text is null || text.Length <= MaxTextLength ? text : text[..MaxTextLength] + "\n\n…";
}
