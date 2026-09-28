using mTiles.Services.Phone.Remote;
using mTiles.ViewModels;

namespace mTiles.Services.Phone;

/// <summary>
/// What a phone can see of the window: its workspaces, their layouts, and the tiles in them.
/// </summary>
/// <remarks>
/// The one seam between the bridge and the view model tree, so the bridge can be tested against a
/// handful of leaves rather than a main window. Every member runs on the UI thread.
/// </remarks>
internal interface IPhoneWorkspaces
{
    IReadOnlyList<RemoteWorkspace> List();

    RemoteLayout? Layout(string workspaceId);

    /// <summary>Opens a workspace on the desktop. False when there is no such workspace.</summary>
    bool Open(string workspaceId);

    /// <summary>A tile by its id, in whichever loaded workspace holds it.</summary>
    (LeafTileNodeViewModel Tile, string WorkspaceId)? Find(string tileId);

    /// <summary>The tile the desktop's keyboard is on — what a phone that has not chosen one reaches.</summary>
    LeafTileNodeViewModel? ActiveTile { get; }
}

/// <summary><see cref="IPhoneWorkspaces"/> over the application's own main view model.</summary>
/// <remarks>Handed a <c>Func</c> rather than the view model, because the bridge is built before the
/// window is — the same circle <c>App</c> already breaks this way for the active tile.</remarks>
internal sealed class MainWindowPhoneWorkspaces(Func<MainWindowViewModel?> main) : IPhoneWorkspaces
{
    private readonly TilePreviews _previews = new();

    public IReadOnlyList<RemoteWorkspace> List()
    {
        if (main() is not { } vm) return [];

        var current = vm.CurrentWorkspace?.WorkspaceId;
        return
        [
            .. vm.WorkspacesPanel.Workspaces.Select(row => new RemoteWorkspace(
                row.Id,
                row.Name,
                string.IsNullOrEmpty(row.BranchName) ? null : row.BranchName,
                RemoteActivity.Of(row.Activity),
                vm.LoadedWorkspaces.ContainsKey(row.Id),
                row.Id == current,
                row.IsFavorite)),
        ];
    }

    public RemoteLayout? Layout(string workspaceId)
    {
        if (main() is not { } vm || !vm.LoadedWorkspaces.TryGetValue(workspaceId, out var workspace))
            return null;

        return new RemoteLayout(workspace.WorkspaceId, workspace.Name,
            LayoutProjection.Project(workspace.RootTile, PhoneTiles.IsReachable,
                leaf => _previews.For(leaf.TileId, PhoneTiles.Preview(leaf))));
    }

    public bool Open(string workspaceId) => main()?.OpenWorkspace(workspaceId) ?? false;

    public (LeafTileNodeViewModel Tile, string WorkspaceId)? Find(string tileId)
    {
        if (main() is not { } vm) return null;

        foreach (var (id, workspace) in vm.LoadedWorkspaces)
        {
            foreach (var leaf in LayoutProjection.Leaves(workspace.RootTile))
            {
                if (leaf.TileId == tileId)
                    return (leaf, id);
            }
        }

        return null;
    }

    public LeafTileNodeViewModel? ActiveTile => main()?.ActiveTile;
}
