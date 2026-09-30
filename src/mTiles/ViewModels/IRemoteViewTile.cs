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
/// Tile content whose next message can carry a photo or a file sent from a phone.
/// </summary>
/// <remarks>The tile keeps the attachment and answers the marker that names it — an image's
/// <c>[Image #n]</c>, a file's <c>@</c> mention — <b>without typing it</b>: the phone puts it into its own box,
/// which is mirrored into the tile's. Pictures arrive as PNG.</remarks>
public interface IRemoteAttachTile : ITile
{
    Task<RemoteAttachResult> AttachFromRemoteAsync(string name, string mimeType, byte[] data);
}

/// <param name="Marker">What to put in the message to name it, or null when it was refused.</param>
/// <param name="Notice">Why it was refused, or what to know about it — a file named where it is.</param>
public sealed record RemoteAttachResult(string? Marker, string? Notice = null);

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
