using mTiles.Services.Phone;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a paired phone is allowed to see and press.
/// </summary>
/// <remarks>
/// The keys a phone can send are a closed enum decided at compile time; tile actions are not — a kind
/// registered later brings whatever it likes, and Git already has Discard changes and Undo last commit.
/// This is the single point at which that open set could quietly become the thing the design replaced,
/// so the rule is pure and it is pinned here rather than left to the page.
/// </remarks>
public sealed class PhoneTileActionsTests
{
    private static readonly TileAction Refresh = new("refresh", "Refresh", "refresh");
    private static readonly TileAction Disabled = new("commit", "Commit", "check", IsEnabled: false);
    private static readonly TileAction Discard = new("discard", "Discard changes", "delete",
        IsDestructive: true);

    /// <summary>
    /// Nothing destructive is offered at all.
    /// </summary>
    /// <remarks>
    /// Not "with a confirmation": confirming on a phone something you cannot see is theatre, and this
    /// codebase already holds that an unwired confirmation answers no.
    /// </remarks>
    [Fact]
    public void A_destructive_action_is_never_sent_to_a_phone()
    {
        var offered = PhoneTileActions.ForPhone([Refresh, Discard, Disabled]);

        Assert.Equal(["refresh", "commit"], offered.Select(a => a.Id));
    }

    /// <summary>An action that opens a window on this machine is neither shown nor pressable: from a
    /// phone it leaves a folder picker waiting on a desktop nobody is at.</summary>
    [Fact]
    public void An_action_needing_the_local_screen_is_not_offered_to_a_phone()
    {
        var add = new TileAction(TileActionIds.Add, "Add Workspace", "plus", NeedsLocalScreen: true);

        Assert.Equal(["refresh"], PhoneTileActions.ForPhone([Refresh, add]).Select(a => a.Id));
        Assert.False(PhoneTileActions.IsAllowed([Refresh, add], TileActionIds.Add));
    }

    /// <summary>And it cannot be reached by naming it either, which is the half that matters: the
    /// filter on the way out is a courtesy, the filter on the way in is the rule.</summary>
    [Fact]
    public void A_destructive_action_cannot_be_pressed_by_name()
    {
        Assert.False(PhoneTileActions.IsAllowed([Refresh, Discard], "discard"));
        Assert.True(PhoneTileActions.IsAllowed([Refresh, Discard], "refresh"));
    }

    /// <summary>
    /// An action the tile cannot do right now is refused as well.
    /// </summary>
    /// <remarks>
    /// Stricter than the keys are: Enter can always be pressed, whereas an action is gated on being
    /// enabled for this tile in this state. The phone's copy of the list is as old as the last state it
    /// was told about, and a tile moves through its own phases without anybody pressing anything.
    /// </remarks>
    [Fact]
    public void A_disabled_action_is_shown_and_refused()
    {
        Assert.Contains(PhoneTileActions.ForPhone([Disabled]), a => a.Id == "commit");
        Assert.False(PhoneTileActions.IsAllowed([Disabled], "commit"));
    }

    /// <summary>An id nothing offers gets the same answer malformed JSON gets: none.</summary>
    [Fact]
    public void An_unknown_id_is_refused()
    {
        Assert.False(PhoneTileActions.IsAllowed([Refresh], "format-the-disk"));
        Assert.False(PhoneTileActions.IsAllowed([], "refresh"));
    }

    /// <summary>What a tile, zoomed into on a phone, offers to press: the same filtered list, so the one
    /// rule decides both what a phone sees and what it may press.</summary>
    [Fact]
    public void A_zoomed_in_tile_offers_only_what_a_phone_may_press()
    {
        var leaf = new LeafTileNodeViewModel("stub", new ActionsOnly([Refresh, Discard, Disabled]), "",
            new TileActivationScope()) { TileName = "Git#1" };

        var view = PhoneTiles.Describe(leaf, "ws");

        Assert.Equal("Git#1", view.Name);
        Assert.Equal("none", view.View);
        Assert.Equal(["refresh", "commit"], view.Actions.Select(a => a.Id));
        Assert.False(view.Actions.Single(a => a.Id == "commit").Enabled);
        Assert.True(PhoneTiles.IsReachable(leaf));
    }

    /// <summary>A tile with nothing a phone may do is drawn in the miniature and cannot be zoomed into.
    /// </summary>
    [Fact]
    public void A_tile_with_nothing_for_a_phone_cannot_be_zoomed_into()
    {
        var leaf = new LeafTileNodeViewModel("stub", new ActionsOnly([Discard]), "", new TileActivationScope());

        Assert.False(PhoneTiles.IsReachable(leaf));
    }

    private sealed class ActionsOnly(IReadOnlyList<TileAction> actions) : ITileActions
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public string KindId => "stub";
        public IReadOnlyList<TileAction> Actions => actions;
        public Task<TileActionResult> InvokeAsync(string id) => Task.FromResult(TileActionResult.Ok);
        public void Dispose() { }
    }

    /// <summary>
    /// Restarting a shell is the one thing a shipped tile keeps from a phone.
    /// </summary>
    /// <remarks>
    /// <para>The filter is the guarantee; this is the check that the guarantee is not doing all the work
    /// silently, and it is written as the exhaustive list rather than as "nothing is destructive" so
    /// that a kind adding a seventh action has to be thought about before the build goes green. Git's
    /// Discard changes and Undo last commit are deliberately not in its
    /// <see cref="ITileActions.Actions"/> at all — they are commands of its own view, where the user can
    /// see what they are about to lose.</para>
    /// <para>Restart shell is here because it kills whatever the shell is running, which is why the
    /// tile header asks first. A phone cannot be asked anything it could answer usefully, so the flag
    /// and not a confirmation is what stands between it and a build somebody had running.</para>
    /// </remarks>
    [Fact]
    public void Restarting_a_shell_is_the_only_thing_a_shipped_tile_withholds()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new mTiles.Services.Tiles.TileContext(directory.Path, settings.Service);

        List<string> withheld = [];
        foreach (var entry in TestTiles.Catalog(settings.Service).Entries)
        {
            var tile = entry.Kind.Create(context, null);
            try
            {
                if (tile is not ITileActions actions) continue;
                var offered = PhoneTileActions.ForPhone(actions.Actions);
                withheld.AddRange(actions.Actions.Except(offered).Select(a => a.Id));
            }
            finally { tile.Dispose(); }
        }

        // Distinct, because three shipped kinds run a process — a terminal, an agent and an agent held as
        // a conversation — and share the restart. The conversation adds the two a phone must never do
        // unseen: throwing a conversation away, and leaving the one on screen for another. Neither is
        // withheld only for being destructive — starting a new conversation destroys nothing now that the
        // old one stays in the store — but both can put a question on a desktop nobody is standing at.
        // What this pins is which actions a phone never sees, not how many tiles offer them.
        Assert.Equal(
            [
                mTiles.ViewModels.AgentConversation.AgentConversationTileViewModel.DeleteConversationActionId,
                mTiles.ViewModels.AgentConversation.AgentConversationTileViewModel.NewConversationActionId,
                TileActionIds.Restart,
            ],
            withheld.Distinct().Order(StringComparer.Ordinal));
    }
}
