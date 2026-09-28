using Avalonia.Layout;
using mTiles.ViewModels;

namespace mTiles.Services.Phone.Remote;

/// <summary>
/// A workspace's tile tree as the phone's miniature of it.
/// </summary>
/// <remarks>
/// <para>The whole tree, whatever the desktop is showing: a tile maximised on the desktop
/// (<see cref="SplitTileNodeViewModel.Solo"/>) is a way of looking at one tile there, and the phone's
/// zoom is its own way of doing the same thing — so the miniature keeps every tile where it stands.</para>
/// <para>Pure over the node types, and what may be zoomed into is asked of the caller, because that is
/// the bridge's rule and not the layout's.</para>
/// </remarks>
public static class LayoutProjection
{
    /// <summary>The smallest share a side is drawn at. A side squeezed to a sliver on a wide monitor
    /// is still a tile somebody put there, and on a phone a sliver cannot be tapped.</summary>
    public const double MinShare = 0.15;

    public static RemoteNode? Project(TileNodeViewModel? node, Func<LeafTileNodeViewModel, bool> reachable,
        Func<LeafTileNodeViewModel, RemotePreview?>? preview = null) =>
        node switch
        {
            LeafTileNodeViewModel leaf => new RemoteLeaf(
                leaf.TileId,
                leaf.KindId,
                leaf.TileName,
                RemoteActivity.Of(leaf.Activity),
                leaf.IsActive,
                reachable(leaf),
                preview?.Invoke(leaf)),

            SplitTileNodeViewModel { First: { } first, Second: { } second } split =>
                Split(split, Project(first, reachable, preview), Project(second, reachable, preview)),

            SplitTileNodeViewModel split => Project(split.First ?? split.Second, reachable, preview),

            _ => null,
        };

    private static RemoteNode? Split(SplitTileNodeViewModel split, RemoteNode? first, RemoteNode? second)
    {
        if (first is null) return second;
        if (second is null) return first;

        // Horizontal is a horizontal divider — rows — which is Avalonia's word for the orientation of the
        // line between the two, not for the direction the children run in.
        var direction = split.Orientation == Orientation.Horizontal ? "column" : "row";
        var ratio = double.IsFinite(split.SplitRatio) ? split.SplitRatio : 0.5;
        return new RemoteSplit(direction, Math.Round(Math.Clamp(ratio, MinShare, 1 - MinShare), 3), first, second);
    }

    /// <summary>Every leaf in the tree, depth first — the order a phone lists them in when it has no
    /// room to draw the miniature.</summary>
    public static IEnumerable<LeafTileNodeViewModel> Leaves(TileNodeViewModel? node)
    {
        switch (node)
        {
            case LeafTileNodeViewModel leaf:
                yield return leaf;
                break;
            case SplitTileNodeViewModel split:
                foreach (var l in Leaves(split.First)) yield return l;
                foreach (var l in Leaves(split.Second)) yield return l;
                break;
        }
    }
}
