using mTiles.Services.Phone.Remote;

namespace mTiles.ViewModels;

/// <summary>
/// Tile content a paired phone can zoom into: what it shows there, and what the phone may ask of it.
/// </summary>
/// <remarks>
/// <para>Announced by interface, as every other capability is (see <c>docs/TILES.md</c>), so the phone
/// bridge never learns which kinds exist. A kind that does not implement it is still drawn in the
/// phone's miniature of the layout — a layout with holes in it is not recognisable — but cannot be
/// zoomed into unless it at least offers actions a phone may press.</para>
/// <para>Everything here runs on the UI thread. <see cref="DescribeForRemote"/> is a snapshot of plain
/// records, so the bridge serialises it elsewhere without touching the view model again.</para>
/// </remarks>
public interface IRemoteViewTile : ITile
{
    /// <summary>
    /// A number that moves whenever <see cref="DescribeForRemote"/> would answer differently.
    /// </summary>
    /// <remarks>What the bridge samples a watched tile by: comparing a counter costs nothing, while
    /// describing a long conversation and comparing the text four times a second would cost a
    /// serialisation of it each time. Only ever compared for equality.</remarks>
    long RemoteVersion { get; }

    /// <summary>What the tile shows on a phone right now.</summary>
    RemoteTileBody DescribeForRemote();

    /// <summary>Does what the phone asked.</summary>
    /// <returns>Null when it was done; otherwise one sentence the phone shows, naming why not.</returns>
    /// <remarks>What a paired device can cause is decided here, in this process, against what the tile
    /// offers <em>now</em> — the phone's picture is as old as the last one it was sent.</remarks>
    Task<RemoteRefusal?> HandleRemoteAsync(RemoteTileCommand command);
}

/// <summary>
/// Tile content that holds one of several stored conversations, and can list them for a phone.
/// </summary>
/// <remarks>Asked on demand rather than pushed: the list is a query across the store and changes only when
/// somebody says something. Opening one is <see cref="RemoteOpenConversation"/>.</remarks>
public interface IRemoteConversationsTile : ITile
{
    Task<IReadOnlyList<RemoteConversation>> ConversationsForRemoteAsync();
}

/// <summary>
/// Tile content that can say, in a line, what it is doing — for its card in a phone's miniature of the
/// layout, so a workspace can be read without zooming into every tile.
/// </summary>
/// <remarks>Separate from <see cref="IRemoteViewTile"/> because a tile the phone draws only as a card
/// still has something worth a line. Asked on the UI thread about once a second while a phone looks at
/// the layout, so it must be cheap: never a description of the whole conversation.</remarks>
public interface IRemotePreviewTile : ITile
{
    TilePreview? PreviewForRemote();
}
