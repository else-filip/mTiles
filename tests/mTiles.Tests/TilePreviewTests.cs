using System.Collections.Immutable;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.Services.Phone.Remote;
using Xunit;

namespace mTiles.Tests;

/// <summary>The one line a tile's card carries in a phone's miniature of the layout.</summary>
public sealed class TilePreviewTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   \n  ", null)]
    [InlineData("## Plan\nstep one", "Plan")]
    [InlineData("---\n\n- **Fixed** the `parser`", "Fixed the parser")]
    [InlineData("1. first   thing", "first thing")]
    [InlineData("> quoted", "quoted")]
    [InlineData("━━━━━━\nreal line", "real line")]
    public void A_line_is_the_first_that_says_anything_without_markdowns_marks(string? text, string? line) =>
        Assert.Equal(line, TilePreviews.OneLine(text));

    [Fact]
    public void A_long_line_is_clipped()
    {
        var line = TilePreviews.OneLine(new string('x', 500))!;
        Assert.Equal(TilePreviews.MaxLength, line.Length);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void A_screen_is_read_from_the_bottom() =>
        Assert.Equal("$ dotnet build", TilePreviews.LastLine(["old output", "$ dotnet build", "   ", ""]));

    /// <summary>A tile that cannot say when it last moved is stamped when its line changes — and not when
    /// it is first seen, since "just now" on a tile idle for an hour is worse than no time at all.</summary>
    [Fact]
    public void A_line_is_stamped_when_it_changes_and_not_when_first_seen()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        var previews = new TilePreviews(() => now);

        Assert.Null(previews.For("t", new TilePreview("a"))!.ChangedAt);
        now = now.AddSeconds(5);
        Assert.Null(previews.For("t", new TilePreview("a"))!.ChangedAt);
        now = now.AddSeconds(5);
        Assert.Equal(11_000, previews.For("t", new TilePreview("b"))!.ChangedAt);
        now = now.AddSeconds(5);
        Assert.Equal(11_000, previews.For("t", new TilePreview("b"))!.ChangedAt);
    }

    [Fact]
    public void A_time_the_tile_knows_wins_over_the_stamp()
    {
        var previews = new TilePreviews(() => DateTimeOffset.FromUnixTimeMilliseconds(99));
        var at = DateTimeOffset.FromUnixTimeMilliseconds(42);
        Assert.Equal(42, previews.For("t", new TilePreview("a", 12, at))!.ChangedAt);
        Assert.Null(previews.For("t", null));
    }

    [Fact]
    public void An_agent_says_what_it_asks_before_anything_else()
    {
        var state = new ConversationState
        {
            Timeline = [new MessageEntry("1", MessageRole.Assistant, "done", false, [])],
            PendingApprovals =
            [
                new ApprovalRequested("req", ApprovalKind.Command, "Run dotnet test?", null, null,
                    [new ApprovalOption(ApprovalDecision.Accept, "Yes")]),
            ],
        };

        Assert.Equal("Allow? Run dotnet test?", AgentChatProjection.Preview(state, 20).Text);
    }

    [Fact]
    public void An_agent_at_work_names_its_step_and_otherwise_what_was_last_said()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(100);
        var working = new ConversationState
        {
            Timeline =
            [
                new MessageEntry("1", MessageRole.User, "fix it", false, []) { At = at },
                new WorkGroupEntry("w", ImmutableList.Create<WorkItem>(
                    new ToolCallItem("a", ToolKind.Other, "t", "Edit Program.cs", new ToolDetail(), "", ToolCallState.Running,
                        at.AddSeconds(3), null))) { At = at.AddSeconds(1) },
            ],
        };
        var preview = AgentChatProjection.Preview(working, 33);
        Assert.Equal(("Edit Program.cs", 33.0, at.AddSeconds(3)), (preview.Text, preview.ContextPercent, preview.ChangedAt));

        var answered = working with
        {
            Timeline = working.Timeline.Add(new MessageEntry("2", MessageRole.Assistant, "## Done\nAll green.", false, [])),
        };
        Assert.Equal("Done", AgentChatProjection.Preview(answered, null).Text);

        var asked = new ConversationState { Timeline = [new MessageEntry("1", MessageRole.User, "fix it", false, [])] };
        Assert.Equal("You: fix it", AgentChatProjection.Preview(asked, null).Text);
        Assert.Null(AgentChatProjection.Preview(new ConversationState(), null).ChangedAt);
    }
}
