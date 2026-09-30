using System.Diagnostics;
using mTiles.ViewModels;
using Tailcat.Link;

namespace mTiles.Services.Phone;

/// <summary>
/// A photo or a file sent from a phone into an Agent or a Goal tile's next message.
/// </summary>
/// <remarks>
/// <para><b>One channel per file</b> (<see cref="PhoneProtocol.AttachChannel"/>), for the reason a recording
/// has one: a request is capped at <see cref="PhoneProtocol.MaxRequestBytes"/>, a photo is megabytes, and
/// what is on the channel stays in order. The first frame is a JSON header, the rest are the bytes, and the
/// channel closing on purpose is "that was all of it" — ended with the session, it is thrown away.</para>
/// <para><b>The tile registers it and does not type its marker</b>: the answer (<c>attached</c>) carries the
/// marker back and the phone puts it into its own box, which is mirrored into the tile's. Typed on both
/// sides, the two boxes would disagree and the mirror would stop at the first keystroke after.</para>
/// <para>A picture reaches the tile in the format the phone sent it: only the Goal tile needs PNG, and
/// re-encoding a photo for the Agent tile would multiply its size past that tile's own limit.</para>
/// </remarks>
internal sealed class PhoneAttachments(IPhoneWorkspaces workspaces, IUiDispatcher dispatcher)
{
    /// <summary>The largest file a phone may send — the size past which the desktop names a file where it is
    /// rather than copying it (<see cref="AttachmentStore.MaxCopyBytes"/>).</summary>
    internal const long MaxBytes = AttachmentStore.MaxCopyBytes;

    internal async Task HandleAsync(ILinkPeer peer, ILinkChannelReader channel, CancellationToken cancellationToken)
    {
        ChannelCloseReason? closed = null;
        channel.Closed += (_, e) => closed = e.Reason;

        PhoneProtocol.AttachHeader? header = null;
        using var body = new MemoryStream();
        var tooLarge = false;

        await foreach (var frame in channel.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (header is null)
            {
                header = PhoneProtocol.ParseAttachHeader(frame.Span);
                if (header is null) return;
                continue;
            }

            if (body.Length + frame.Length > MaxBytes) tooLarge = true;
            if (!tooLarge) body.Write(frame.Span);
        }

        if (header is null || closed != ChannelCloseReason.PeerClosed) return;

        var answer = tooLarge
            ? new { id = header.Id, tileId = header.TileId, error = $"That file is larger than {MaxBytes / (1024 * 1024)} MB." }
            : await AttachAsync(header, body.ToArray()).ConfigureAwait(false);
        await PhoneBridgeManager.NotifyAsync(peer, PhoneProtocol.Push("attached", answer)).ConfigureAwait(false);
    }

    private Task<object> AttachAsync(PhoneProtocol.AttachHeader header, byte[] data) =>
        dispatcher.InvokeAsync(async () =>
        {
            if (workspaces.Find(header.TileId)?.Tile.Content is not IRemoteAttachTile tile)
                return (object)new { id = header.Id, tileId = header.TileId, error = "That tile takes no attachments." };

            try
            {
                var result = await tile.AttachFromRemoteAsync(header.Name, header.Mime, data).ConfigureAwait(true);
                return result.Marker is { } marker
                    ? new { id = header.Id, tileId = header.TileId, marker, notice = result.Notice }
                    : new { id = header.Id, tileId = header.TileId, error = result.Notice ?? "That could not be attached." };
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("An attachment from a phone failed: {0}", ex);
                return new { id = header.Id, tileId = header.TileId, error = "mTiles could not read that file." };
            }
        }).Unwrap();
}
