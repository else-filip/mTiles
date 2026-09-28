using Tailcat.Keys;
using Tailcat.Link;

namespace mTiles.Services.Phone;

/// <summary>What each paired phone is looking at, what it was last sent, and so what it needs sending
/// now.</summary>
/// <remarks>Keyed by the peer's public key, which is the one thing about a phone that stays the same
/// across its reconnections. The link and the sending are <see cref="PhoneBridgeManager"/>'s; this only
/// decides.</remarks>
internal sealed class PhonePushPlan(IPhoneWorkspaces workspaces)
{
    private readonly Dictionary<NodePublic, PeerState> _peers = [];
    private readonly Lock _gate = new();

    public void Clear()
    {
        lock (_gate) _peers.Clear();
    }

    public void Remove(NodePublic peer)
    {
        lock (_gate) _peers.Remove(peer);
    }

    /// <summary>Everything is sent again: a phone that reconnects has a page that may have been
    /// reloaded.</summary>
    public void ForgetWhatWasSent(NodePublic peer)
    {
        lock (_gate)
        {
            if (_peers.TryGetValue(peer, out var state))
                state.Forget();
        }
    }

    public void Watch(NodePublic peer, string? workspaceId, string? tileId)
    {
        lock (_gate)
        {
            var state = StateOf(peer);
            state.WorkspaceId = workspaceId;
            state.TileId = tileId;
            state.Forget();
        }
    }

    /// <summary>What each phone needs sending now. UI thread only.</summary>
    /// <param name="full">Whether to look at the slow-moving parts too — the workspace list and the
    /// layout, which only change when somebody changes them and are compared once a second.</param>
    public List<(ILinkPeer Peer, List<byte[]> Messages)> Collect(IReadOnlyList<ILinkPeer> peers, byte[] session, bool full)
    {
        var result = new List<(ILinkPeer, List<byte[]>)>();
        var describedThisTick = new Dictionary<string, DescribedTile>();

        lock (_gate)
        {
            foreach (var peer in peers)
            {
                var messages = MessagesFor(StateOf(peer.Key), session, full, describedThisTick);
                if (messages.Count > 0)
                    result.Add((peer, messages));
            }
        }

        return result;
    }

    private List<byte[]> MessagesFor(PeerState state, byte[] session, bool full,
        Dictionary<string, DescribedTile> describedThisTick)
    {
        var messages = new List<byte[]>();

        if (state.Offer(ref state.LastSession, session))
            messages.Add(session);

        if (full || state.LastWorkspaces is null)
        {
            var list = PhoneProtocol.Push("workspaces", new { workspaces = workspaces.List() });
            if (state.Offer(ref state.LastWorkspaces, list))
                messages.Add(list);
        }

        if (state.WorkspaceId is { } workspaceId && (full || state.LastLayout is null))
        {
            var layout = PhoneProtocol.Push("layout", new { layout = workspaces.Layout(workspaceId) });
            if (state.Offer(ref state.LastLayout, layout))
                messages.Add(layout);
        }

        if (state.TileId is { } tileId)
        {
            if (!describedThisTick.TryGetValue(tileId, out var tile))
                describedThisTick[tileId] = tile = DescribeTile(tileId);

            // Described only when its content or its header moved, and once per tick however many
            // phones watch it: a streaming conversation is the one message here that is large.
            if (tile.Signature != state.LastTileSignature || state.LastTile is null)
            {
                state.LastTileSignature = tile.Signature;
                if (state.Offer(ref state.LastTile, tile.Message.Value))
                    messages.Add(tile.Message.Value);
            }
        }

        return messages;
    }

    /// <summary>A tile's cheap signature, and its message built only if some phone needs it.</summary>
    private sealed record DescribedTile(string Signature, Lazy<byte[]> Message);

    private DescribedTile DescribeTile(string tileId)
    {
        if (workspaces.Find(tileId) is not { } hit)
            return new DescribedTile("gone", new Lazy<byte[]>(() => PhoneProtocol.Push("tileGone", new { tileId })));

        var signature = PhoneTiles.SignatureOf(hit.Tile);
        return new DescribedTile(signature, new Lazy<byte[]>(() =>
            PhoneProtocol.Push("tile", new { tile = PhoneTiles.Describe(hit.Tile, hit.WorkspaceId) })));
    }

    private PeerState StateOf(NodePublic peer)
    {
        if (!_peers.TryGetValue(peer, out var state))
            _peers[peer] = state = new PeerState();
        return state;
    }

    /// <summary>What one phone is looking at, and what it was last sent.</summary>
    private sealed class PeerState
    {
        public string? WorkspaceId;
        public string? TileId;

        public byte[]? LastSession;
        public byte[]? LastWorkspaces;
        public byte[]? LastLayout;
        public byte[]? LastTile;
        public string? LastTileSignature;

        /// <summary>True, and remembered, when <paramref name="message"/> differs from what was last sent
        /// in that slot.</summary>
        public bool Offer(ref byte[]? last, byte[] message)
        {
            if (last is not null && last.AsSpan().SequenceEqual(message)) return false;
            last = message;
            return true;
        }

        public void Forget()
        {
            LastSession = LastWorkspaces = LastLayout = LastTile = null;
            LastTileSignature = null;
        }
    }
}
