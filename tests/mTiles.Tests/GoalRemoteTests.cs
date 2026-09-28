using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Phone.Remote;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a paired phone may cause in a Goal tile: a press is answered only while the thing it names is
/// the thing the tile is waiting on now.
/// </summary>
/// <remarks>
/// The phone's picture is as old as the last push, so every rule here is about a stale press — one that,
/// accepted, would approve a plan nobody read or carry a run past a review nobody saw.
/// </remarks>
public class GoalRemoteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-goal-remote-" + Guid.NewGuid());

    public GoalRemoteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
    }

    [Fact]
    public void A_plan_approval_names_the_plan_it_was_shown()
    {
        Ui.Run(() =>
        {
            using var tile = TileWith(new GoalTileState
            {
                OriginalGoal = "a goal",
                CurrentPhase = GoalPhase.Plan,
                ProposedPlan = "1. Do the thing.",
            });
            var shown = PendingOf(tile);
            Assert.Equal("plan", shown.Kind);

            using var other = TileWith(new GoalTileState
            {
                OriginalGoal = "a goal",
                CurrentPhase = GoalPhase.Plan,
                ProposedPlan = "1. Do another thing.",
            });

            Assert.Equal("The plan has changed since the phone showed it.",
                Handle(other, new RemoteChoose(shown.Id, "approve")));
        });
    }

    [Fact]
    public void A_gate_press_is_refused_where_no_review_is_waiting()
    {
        Ui.Run(() =>
        {
            using var tile = TileWith(new GoalTileState { OriginalGoal = "a goal", CurrentPhase = GoalPhase.Implement });

            Assert.Equal("The review is no longer waiting.", Handle(tile, new RemoteChoose("gate:00", "continue")));
        });
    }

    [Fact]
    public void Answers_to_a_round_that_is_not_waiting_are_refused()
    {
        Ui.Run(() =>
        {
            using var tile = TileWith(new GoalTileState { OriginalGoal = "a goal", CurrentPhase = GoalPhase.Implement });

            Assert.Equal("Those questions have already been answered.",
                Handle(tile, new RemoteAnswer("clarify:00", new Dictionary<string, IReadOnlyList<string>>())));
        });
    }

    [Fact]
    public void A_new_goal_that_would_ask_to_discard_this_one_is_left_to_the_computer()
    {
        Ui.Run(() =>
        {
            var state = new GoalTileState { OriginalGoal = "a goal", CurrentPhase = GoalPhase.Summary };
            state.Messages.Add(new GoalMessage { Role = GoalMessageRole.User, Text = "a goal" });
            using var tile = TileWith(state);

            Assert.StartsWith("Starting a new goal discards this one",
                Handle(tile, new RemoteSendText("another goal", Submit: true)));
        });
    }

    private static RemotePending PendingOf(GoalTileViewModel tile) =>
        Assert.IsType<RemotePending>(tile.DescribeForRemote().Chat?.Pending);

    private static string? Handle(GoalTileViewModel tile, RemoteTileCommand command) =>
        tile.HandleRemoteAsync(command).GetAwaiter().GetResult();

    private GoalTileViewModel TileWith(GoalTileState state)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        new GoalStatePersistence().Save(path, state);

        var settings = new SettingsService(Path.Combine(_dir, "settings.json"));
        return new GoalTileViewModel(path, _dir, settings) { ConfirmAction = _ => Task.FromResult(true) };
    }
}
