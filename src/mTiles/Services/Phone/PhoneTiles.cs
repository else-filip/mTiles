using mTiles.Services.Phone.Remote;
using mTiles.ViewModels;

namespace mTiles.Services.Phone;

/// <summary>
/// One tile as a phone sees it and drives it — the part that is the same for every kind.
/// </summary>
/// <remarks>
/// A kind that implements <see cref="IRemoteViewTile"/> says what it shows and answers its own commands;
/// every other tile is still something a phone can type into or press an action on, if it offers either.
/// Decided here, once, so the miniature's "can this be tapped" and the zoomed-in view's "what can be done
/// here" cannot disagree about a tile.
/// </remarks>
internal static class PhoneTiles
{
    /// <summary>Whether a phone can zoom into this tile at all.</summary>
    public static bool IsReachable(LeafTileNodeViewModel leaf) => leaf.Content switch
    {
        null => false,
        IRemoteViewTile or ITextInputTile => true,
        _ => PhoneTileActions.ForPhone(leaf.Actions).Count > 0,
    };

    /// <summary>The line on the tile's card in the miniature, or null for a kind with nothing to say.
    /// UI thread only.</summary>
    /// <remarks>Wrapped: this runs every tile's own code once a second, and one tile throwing must cost
    /// its card a line, not the whole layout.</remarks>
    public static TilePreview? Preview(LeafTileNodeViewModel leaf)
    {
        try
        {
            return (leaf.Content as IRemotePreviewTile)?.PreviewForRemote();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("A tile's phone preview failed: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>The tile, zoomed into. UI thread only.</summary>
    public static RemoteTileView Describe(LeafTileNodeViewModel leaf, string workspaceId)
    {
        var body = leaf.Content switch
        {
            IRemoteViewTile remote => remote.DescribeForRemote(),
            ITextInputTile => new RemoteTileBody("none", new RemoteStatus(RemoteActivity.Of(leaf.Activity)),
                Composer: new RemoteComposer(true, "Type into this tile", Keys: true)),
            _ => new RemoteTileBody("none", new RemoteStatus(RemoteActivity.Of(leaf.Activity))),
        };

        return new RemoteTileView(
            leaf.TileId,
            workspaceId,
            leaf.TileName,
            leaf.KindId,
            body.View,
            body.Status,
            body.Chat,
            body.Screen,
            body.Composer ?? RemoteComposer.None,
            [.. PhoneTileActions.ForPhone(leaf.Actions).Select(a => new RemoteAction(a.Id, a.Label, a.Icon, a.IsEnabled))],
            body.NewLabel,
            ListsConversations: leaf.Content is IRemoteConversationsTile);
    }

    /// <summary>
    /// What a tile's content changes by, for the push loop to compare without describing it.
    /// </summary>
    /// <remarks>A tile that is not an <see cref="IRemoteViewTile"/> has nothing moving except its name,
    /// its activity and its actions, which the loop compares by describing it — cheap for a tile that
    /// shows nothing.</remarks>
    public static long VersionOf(LeafTileNodeViewModel leaf) =>
        leaf.Content is IRemoteViewTile remote ? remote.RemoteVersion : -1;

    /// <summary>
    /// Everything a description of the tile depends on, without describing it: the content's version plus
    /// the leaf's own name, activity and actions, which are not in that version. UI thread only.
    /// </summary>
    public static string SignatureOf(LeafTileNodeViewModel leaf) => string.Join('',
        VersionOf(leaf), leaf.TileName, leaf.KindId, leaf.Activity,
        string.Join('', PhoneTileActions.ForPhone(leaf.Actions).Select(a => $"{a.Id}:{a.Label}:{a.Icon}:{a.IsEnabled}")));

    /// <summary>Does what the phone asked, in this tile. UI thread only.</summary>
    /// <returns>Null when done, otherwise the sentence the phone shows.</returns>
    public static async Task<RemoteRefusal?> HandleAsync(LeafTileNodeViewModel leaf, RemoteTileCommand command)
    {
        if (leaf.Content is IRemoteViewTile remote)
            return await remote.HandleRemoteAsync(command).ConfigureAwait(true);

        var input = leaf.Content as ITextInputTile;
        return command switch
        {
            RemoteSendText send when input is not null =>
                input.TrySendText(send.Text, send.Submit) ? null : "That tile is not taking input right now.",
            RemoteKey key when input is not null =>
                input.TryPressKey(key.Key) ? null : "That tile is not taking keys right now.",
            _ => "That tile cannot do that from a phone.",
        };
    }
}
