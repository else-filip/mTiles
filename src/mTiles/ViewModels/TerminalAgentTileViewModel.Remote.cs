using System.Diagnostics;
using mTiles.Services.Agents;
using mTiles.Services.Agents.SessionLogs;
using mTiles.Services.Phone.Remote;

namespace mTiles.ViewModels;

/// <summary>
/// A terminal agent tile as a phone sees it: the screen, and — where the CLI's own record of the
/// conversation can be read — the conversation itself, drawn the way the Agent tile's is.
/// </summary>
/// <remarks>
/// <para><b>Two views of one tile, because each answers what the other cannot.</b> The screen is the last
/// frame of a TUI drawn for the computer's width: it is what the agent is asking <em>now</em> — a menu, a
/// yes/no — and it is unreadable on a phone past a few lines. The transcript is what was said, as text a
/// phone can set in its own type, and it knows nothing of a menu on screen. The page offers both.</para>
/// <para><b>Read only while a phone is looking.</b> A transcript is the whole conversation, re-read from
/// the CLI's store; doing that on every store change for a tile nobody is watching on a phone would cost
/// a parse of a growing file for nothing.</para>
/// </remarks>
public sealed partial class TerminalAgentTileViewModel
{
    /// <summary>How many of the latest messages a phone is sent.</summary>
    internal const int RemoteTranscriptTurns = 80;

    /// <summary>How long after the last look from a phone the transcript is still kept current.</summary>
    private static readonly TimeSpan PhoneWatchWindow = TimeSpan.FromSeconds(30);

    /// <summary>At most one read this often — the screen moves many times a second while the agent works.</summary>
    private static readonly TimeSpan TranscriptReadInterval = TimeSpan.FromSeconds(2);

    private IReadOnlyList<TranscriptTurn> _transcript = [];
    private long _transcriptVersion;
    private DateTime _phoneLookedAt = DateTime.MinValue;
    private DateTime _transcriptReadAt = DateTime.MinValue;
    private bool _readingTranscript;

    /// <summary>Whether this tile's conversation can be shown as one.</summary>
    private bool ShowsTranscript => _agent.SessionLog is { ReadsTranscripts: true } && SessionId.Length > 0;

    /// <inheritdoc />
    public override long RemoteVersion => base.RemoteVersion * 31 + Interlocked.Read(ref _transcriptVersion);

    /// <inheritdoc />
    public override RemoteTileBody DescribeForRemote()
    {
        var body = base.DescribeForRemote();
        if (!ShowsTranscript) return body;

        _phoneLookedAt = DateTime.UtcNow;
        ReadTranscriptSoon();
        return body with { Chat = ChatOf(_transcript) };
    }

    /// <summary>The transcript as the chat a phone draws: the latest turns, and how many were left out.</summary>
    internal static RemoteChat ChatOf(IReadOnlyList<TranscriptTurn> turns)
    {
        var skip = Math.Max(0, turns.Count - RemoteTranscriptTurns);
        var items = turns.Skip(skip).Select((turn, i) => new RemoteChatItem(
            $"t{skip + i}",
            turn.FromUser ? "user" : "assistant",
            turn.Text,
            Markdown: !turn.FromUser)).ToList();
        return new RemoteChat(items, skip, null, null);
    }

    /// <summary>The store changed: read it again if a phone is looking.</summary>
    private void OnStoreChangedForPhone()
    {
        if (DateTime.UtcNow - _phoneLookedAt < PhoneWatchWindow) ReadTranscriptSoon(force: true);
    }

    private void ReadTranscriptSoon(bool force = false)
    {
        if (_readingTranscript || IsDisposed) return;
        if (!force && DateTime.UtcNow - _transcriptReadAt < TranscriptReadInterval) return;
        if (_agent.SessionLog is not { ReadsTranscripts: true } log) return;

        _readingTranscript = true;
        _transcriptReadAt = DateTime.UtcNow;
        var sessionId = SessionId;
        _ = ReadTranscriptAsync(log, sessionId);
    }

    private async Task ReadTranscriptAsync(IAgentSessionLog log, string sessionId)
    {
        try
        {
            var turns = await log.ReadTranscriptAsync(AiSignInStore.Find(_settings.Settings, Instance.SignInId),
                WorkingDirectory, sessionId).ConfigureAwait(false);
            _post(() =>
            {
                _readingTranscript = false;
                if (IsDisposed || sessionId != SessionId || SameTurns(turns, _transcript)) return;
                _transcript = turns;
                Interlocked.Increment(ref _transcriptVersion);
            });
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Reading the transcript of tile {0} for a phone failed: {1}", TileId, ex.Message);
            _post(() => _readingTranscript = false);
        }
    }

    private static bool SameTurns(IReadOnlyList<TranscriptTurn> a, IReadOnlyList<TranscriptTurn> b) =>
        a.Count == b.Count && (a.Count == 0 || a[^1] == b[^1]);
}
