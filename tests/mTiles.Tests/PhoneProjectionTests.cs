using System.Collections.Immutable;
using Avalonia.Layout;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.Models;
using mTiles.Services.Phone.Remote;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a phone is shown of a conversation, a goal and a layout.
/// </summary>
/// <remarks>
/// The projections are pure over the same state the desktop draws from, so these pin what is kept, what
/// is folded and what is left out — the choices that decide whether a transcript read on a phone is the
/// desktop's transcript.
/// </remarks>
public sealed class PhoneProjectionTests
{
    private static MessageEntry Message(string id, MessageRole role, string text, bool streaming = false) =>
        new(id, role, text, streaming, []);

    private static ToolCallItem Tool(string id, string title, ToolCallState state) =>
        new(id, ToolKind.Other, "tool", title, new ToolDetail(), "output nobody on a phone reads", state,
            DateTimeOffset.UnixEpoch, null);

    [Fact]
    public void Messages_travel_as_markdown_with_their_streaming_flag()
    {
        var state = new ConversationState
        {
            Timeline = [Message("1", MessageRole.User, "fix it"), Message("2", MessageRole.Assistant, "**on it**", true)],
        };

        var chat = AgentChatProjection.Project(state);

        Assert.Equal(["user", "assistant"], chat.Items.Select(i => i.Role));
        Assert.All(chat.Items, i => Assert.True(i.Markdown));
        Assert.True(chat.Items[1].Streaming);
        Assert.Equal(0, chat.Omitted);
    }

    /// <summary>A work group is one line per step — what it did and whether it worked — and never the
    /// tool's output, which is what the desk is for.</summary>
    [Fact]
    public void A_work_group_is_one_line_per_step_without_its_output()
    {
        var state = new ConversationState
        {
            Timeline =
            [
                new WorkGroupEntry("w", ImmutableList.Create<WorkItem>(
                    Tool("a", "Read Program.cs", ToolCallState.Completed),
                    new ReasoningItem("r", "thinking out loud"),
                    Tool("b", "dotnet test", ToolCallState.Running),
                    Tool("c", "rm -rf", ToolCallState.Declined))),
            ],
        };

        var item = Assert.Single(AgentChatProjection.Project(state).Items);

        Assert.Equal("work", item.Role);
        Assert.Equal("3 steps, 1 running", item.Text);
        Assert.Equal(
            [("Read Program.cs", "done"), ("dotnet test", "running"), ("rm -rf", "declined")],
            item.Work!.Select(w => (w.Title, w.State)));
        Assert.DoesNotContain("output nobody", item.Text);
    }

    [Fact]
    public void Only_the_newest_entries_travel_and_the_rest_are_counted()
    {
        var state = new ConversationState
        {
            Timeline = [.. Enumerable.Range(0, 10).Select(i => (TimelineEntry)Message($"{i}", MessageRole.User, $"m{i}"))],
        };

        var chat = AgentChatProjection.Project(state, maxItems: 4);

        Assert.Equal(["m6", "m7", "m8", "m9"], chat.Items.Select(i => i.Text));
        Assert.Equal(6, chat.Omitted);
    }

    [Fact]
    public void An_approval_is_offered_with_the_agents_own_answers()
    {
        var state = new ConversationState
        {
            PendingApprovals =
            [
                new ApprovalRequested("req", ApprovalKind.Command, "Run dotnet test?", "dotnet test", null,
                    [new ApprovalOption(ApprovalDecision.Accept, "Yes"), new ApprovalOption(ApprovalDecision.Cancel, "No, stop")]),
            ],
        };

        var pending = AgentChatProjection.Pending(state)!;

        Assert.Equal(("approval", "req"), (pending.Kind, pending.Id));
        Assert.Equal([("Accept", "Yes", "primary"), ("Cancel", "No, stop", "danger")],
            pending.Options.Select(o => (o.Id, o.Label, o.Tone)));

        Assert.True(AgentChatProjection.TryDecision("Accept", out var decision));
        Assert.Equal(ApprovalDecision.Accept, decision);
        Assert.False(AgentChatProjection.TryDecision("42", out _));
        Assert.False(AgentChatProjection.TryDecision("accept", out _));
    }

    /// <summary>A press answers the approval it names by id, with an answer that approval offers — a late
    /// press for one already answered, or an option it never showed, answers nothing.</summary>
    [Theory]
    [InlineData("second", "Accept", "second", null)]
    [InlineData("first", "Accept", null, "That question has already been answered.")]
    [InlineData("second", "AcceptForSession", null, "That is not one of the answers on offer.")]
    [InlineData("second", "42", null, "That is not one of the answers on offer.")]
    public void A_phone_press_answers_only_the_approval_it_names(
        string pendingId, string optionId, string? answered, string? refusal)
    {
        var state = new ConversationState
        {
            PendingApprovals =
            [
                new ApprovalRequested("other", ApprovalKind.Command, "Delete?", "rm", null,
                    [new ApprovalOption(ApprovalDecision.AcceptForSession, "Always")]),
                new ApprovalRequested("second", ApprovalKind.Command, "Run?", "dotnet test", null,
                    [new ApprovalOption(ApprovalDecision.Accept, "Yes"), new ApprovalOption(ApprovalDecision.Cancel, "No")]),
            ],
        };

        var choice = AgentChatProjection.ResolveChoice(state, pendingId, optionId, out var why);

        Assert.Equal(answered, choice?.Approval.RequestId);
        Assert.Equal(refusal, why);
    }

    [Fact]
    public void A_round_of_questions_is_offered_when_nothing_needs_approving()
    {
        var state = new ConversationState
        {
            PendingQuestions =
            [
                new QuestionsAsked("round", [new UserQuestion("q", "Scope", "Which one?", [new QuestionOption("A")], false, true)]),
            ],
        };

        var pending = AgentChatProjection.Pending(state)!;

        Assert.Equal("questions", pending.Kind);
        var question = Assert.Single(pending.Questions!);
        Assert.Equal(("q", "Scope", "Which one?", true), (question.Id, question.Header, question.Text, question.Custom));
        Assert.Equal(["A"], question.Options);
    }

    [Fact]
    public void A_goal_review_is_one_line_per_finding()
    {
        var review = new GoalMessage
        {
            Role = GoalMessageRole.Assistant,
            Text = "Review: 1 error",
            Findings = [new GoalFinding { Severity = GoalSeverity.Error, File = "a.cs", Line = 3, Title = "Off by one" }],
        };

        var chat = GoalChatProjection.Project([new GoalMessage { Role = GoalMessageRole.User, Text = "goal" }, review], null);

        Assert.Equal(["user", "assistant"], chat.Items.Select(i => i.Role));
        Assert.Contains("error  a.cs:3", chat.Items[1].Text);
        Assert.Contains("Off by one", chat.Items[1].Text);
        Assert.False(chat.Items[1].Markdown);
    }

    [Fact]
    public void A_layout_keeps_its_shape_and_says_which_tiles_can_be_reached()
    {
        var scope = new TileActivationScope();
        var left = new LeafTileNodeViewModel("terminal", null, "", scope) { TileName = "left" };
        var top = new LeafTileNodeViewModel("note", null, "", scope) { TileName = "top" };
        var bottom = new LeafTileNodeViewModel("goal", null, "", scope) { TileName = "bottom" };
        var right = new SplitTileNodeViewModel(Orientation.Horizontal, top, bottom) { SplitRatio = 0.02 };
        var root = new SplitTileNodeViewModel(Orientation.Vertical, left, right) { SplitRatio = 0.4 };

        var projected = LayoutProjection.Project(root, leaf => leaf.KindId != "note");

        var split = Assert.IsType<RemoteSplit>(projected);
        Assert.Equal(("row", 0.4), (split.Direction, split.Ratio));
        Assert.Equal("left", Assert.IsType<RemoteLeaf>(split.First).Name);

        // A side squeezed to a sliver on a wide monitor is still drawn wide enough to tap.
        var inner = Assert.IsType<RemoteSplit>(split.Second);
        Assert.Equal(("column", LayoutProjection.MinShare), (inner.Direction, inner.Ratio));
        Assert.False(Assert.IsType<RemoteLeaf>(inner.First).Reachable);
        Assert.True(Assert.IsType<RemoteLeaf>(inner.Second).Reachable);

        Assert.Equal(["left", "top", "bottom"], LayoutProjection.Leaves(root).Select(l => l.TileName));
    }
}
