using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Phone.Remote;
using mTiles.Services.Speech;
using mTiles.ViewModels;
using Tailcat.Link;
using Tailcat.Keys;
using Tailcat.Link.Storage;

namespace mTiles.Services.Phone;

/// <summary>
/// The phone bridge as one thing: the link to the relay, who is paired, what each phone is looking at,
/// and the route from a phone's microphone into a tile.
/// </summary>
/// <remarks>
/// <para><b>Nothing here listens to the network.</b> Both this machine and the phone dial <em>out</em> to
/// Tailscale's public DERP relays through tailcat-link, which passes sealed bytes between two public keys
/// and cannot read them — so there is no port, no certificate and no firewall rule, and it works from the
/// sofa and from the other side of the country alike. The Kestrel server, the self-signed certificates,
/// the firewall repair and the address ranking this replaced all existed only because a port had to be
/// opened and found. See <c>docs/adr/0006-phone-over-relays.md</c>.</para>
/// <para>What is protected is the <b>keyboard</b>: a paired phone can type into a terminal. Pairing is a
/// single-use invitation code shown as a QR code for a few minutes, and a device is unpaired from the
/// panel — after which the host refuses it however often it comes back.</para>
/// <para>Modelled on <see cref="Database.DatabaseServiceManager"/> — one object the application starts
/// and stops, raising <see cref="StateChanged"/> for the UI to redraw from.</para>
/// </remarks>
public sealed class PhoneBridgeManager : IAsyncDisposable
{
    /// <summary>How many phones may be paired at once. A security bound, not a resource one: every
    /// paired device can type into the terminals.</summary>
    public const int MaxDevices = 4;

    /// <summary>How long a QR code on screen can be used to pair.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly SettingsService _settings;
    private readonly PhoneDictation _phoneDictation;
    private readonly IPhoneWorkspaces _workspaces;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<LinkOptions> _linkOptions;
    private readonly TimeSpan _pushInterval;

    /// <summary>Serialises every start and stop.</summary>
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private ILinkHost? _host;
    private volatile bool _disposed;

    /// <summary>How many panels are open. The link stays up while any of them is.</summary>
    private int _holds;

    /// <summary>The setting as last acted on, so an unrelated save does nothing.</summary>
    private bool _appliedEnabled;

    /// <summary>What each paired phone is looking at and has been sent.</summary>
    private readonly PhonePushPlan _plan;

    private Timer? _pushTimer;

    /// <summary>How long a failed start waits before trying the relay again.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    // One-shot: a start that failed while the link is wanted tries again, because nothing else will —
    // a machine that woke before its Wi-Fi would otherwise stay unreachable to its phone until restarted.
    private Timer? _retryTimer;
    private int _pushing;

    /// <summary>A sampling was asked for while one was running, so another is owed as soon as it ends —
    /// a phone that asked to watch a tile must not wait for the timer, which the tests do not run.</summary>
    private int _pushRequested;

    /// <summary>A sampling had to leave a phone out because its previous messages were still on their way;
    /// another is owed when those arrive. Separate from <see cref="_pushRequested"/>, which is repaid at
    /// once: repaying this one at once would only leave the same phone out again, as fast as it could.</summary>
    private int _pushOwedAfterSend;
    private long _tick;

    /// <summary>The phones whose previous messages are still on their way; each is left out of a sampling
    /// until they arrive, so a phone that stopped answering holds back nobody but itself.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Tailcat.Keys.NodePublic, byte> _sending = new();


    /// <param name="linkOptions">What the link is built with. The tests hand in an in-memory relay; the
    /// application leaves it null and gets Tailscale's relays and a store in this application's own
    /// directory.</param>
    /// <param name="pushInterval">How often what the phones are looking at is sampled. Zero starts no
    /// timer — the tests push by hand.</param>
    internal PhoneBridgeManager(
        SettingsService settings,
        DictationService dictation,
        RoutedAudioCapture router,
        IPhoneWorkspaces workspaces,
        IUiDispatcher? dispatcher = null,
        Func<LinkOptions>? linkOptions = null,
        TimeSpan? pushInterval = null)
    {
        _settings = settings;
        _workspaces = workspaces;
        _plan = new PhonePushPlan(workspaces);
        _dispatcher = dispatcher ?? new AvaloniaUiDispatcher();
        _linkOptions = linkOptions ?? DefaultLinkOptions;
        _pushInterval = pushInterval ?? TimeSpan.FromMilliseconds(250);
        _appliedEnabled = settings.Settings.Phone.Enabled;

        _phoneDictation = new PhoneDictation(settings, dictation, router, workspaces, _dispatcher);
        _phoneDictation.Changed += OnDictationChanged;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>Raised when the link starts or stops, or a device pairs, connects or leaves. On any
    /// thread.</summary>
    public event Action? StateChanged;

    /// <summary>What has the keyboard right now, wired from the window.</summary>
    /// <remarks>Only for a phone that has not chosen a tile: it dictates the way the Alt+Space shortcut
    /// does, into the focused text control before the active tile. A phone that has zoomed into a tile
    /// is aiming at that tile, and the words go there whatever has the focus on the desktop.</remarks>
    internal Func<Avalonia.Input.IInputElement?>? FocusedElement
    {
        get => _phoneDictation.FocusedElement;
        set => _phoneDictation.FocusedElement = value;
    }

    /// <summary>The settings this bridge reads, so a view that has the bridge need not also be handed them.</summary>
    public SettingsService Settings => _settings;

    public bool IsRunning => _host is not null;

    /// <summary>Why the last start failed, for the panel to show. Null when it did not.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether the link should be up of its own accord: asked to stay connected, or a phone is
    /// paired and would otherwise have nothing to reach.</summary>
    internal bool ShouldKeepRunning =>
        _settings.Settings.Phone.Enabled || _settings.Settings.Phone.HasPairedDevices;

    /// <summary>The phones paired with this machine, as the panel lists them.</summary>
    internal IReadOnlyList<PhoneDevice> Devices =>
        _host is { } host
            ? [.. host.Peers.Select(p => new PhoneDevice(p, p.Name ?? "Phone", p.IsConnected, p.PairedAt, p.LastSeen))]
            : [];

    /// <summary>How many paired phones have a session up right now.</summary>
    internal int ConnectedDevices => _host?.Peers.Count(p => p.IsConnected) ?? 0;

    private static LinkOptions DefaultLinkOptions() => new()
    {
        Store = new FileLinkStore(AppPaths.GetPhoneDirectory()),
        MaxPeers = MaxDevices,
        PairingWindow = CodeLifetime,
        LoggerFactory = new TraceLoggerFactory(),
    };

    // ── lifetime ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Connects to the relay. Returns false and sets <see cref="LastError"/> on failure.</summary>
    internal async Task<bool> StartAsync()
    {
        if (_disposed) return false;

        try { await _lifecycle.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return false; }

        bool started;
        try
        {
            // Disposed while this call waited for the lock: a host started now would be one nobody stops.
            if (_disposed) return false;
            started = await StartCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }

        // Outside the lock: a handler is free to ask this object to start or stop, and the semaphore is
        // not reentrant.
        if (!started) ScheduleRetryIfWanted();
        StateChanged?.Invoke();
        return started;
    }

    private void ScheduleRetryIfWanted()
    {
        if (_disposed || !(ShouldKeepRunning || Volatile.Read(ref _holds) > 0)) return;
        Interlocked.Exchange(ref _retryTimer,
            new Timer(_ => _ = StartAsync(), null, RetryDelay, Timeout.InfiniteTimeSpan))?.Dispose();
    }

    private void CancelRetry() => Interlocked.Exchange(ref _retryTimer, null)?.Dispose();

    private async Task<bool> StartCoreAsync()
    {
        CancelRetry();
        if (_host is not null) return true;
        LastError = null;

        try
        {
            var host = await TailcatLink.HostManyAsync(PhoneProtocol.AppName, _linkOptions()).ConfigureAwait(false);
            host.SetRequestHandler(HandleRequestAsync);
            host.OnChannel(PhoneProtocol.AudioChannel, _phoneDictation.HandleAudioAsync);
            host.PeerJoined += OnPeerJoined;
            host.PeerLeft += OnPeerLeft;
            _host = host;

            RememberWhetherPaired(host.Peers.Count > 0);

            if (_pushInterval > TimeSpan.Zero)
                _pushTimer = new Timer(_ => _ = PushNowAsync(), null, _pushInterval, _pushInterval);

            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("The phone link could not be started: {0}", ex);
            LastError = ex is LinkException ? ex.Message : "mTiles could not reach the relay. Check the connection.";
            return false;
        }
    }

    /// <summary>Stops the link. Paired phones stay paired and reconnect when it next starts.</summary>
    private async Task StopCoreAsync()
    {
        CancelRetry();
        if (_pushTimer is { } timer)
        {
            _pushTimer = null;
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        if (_host is not { } host) return;
        _host = null;

        host.PeerJoined -= OnPeerJoined;
        host.PeerLeft -= OnPeerLeft;

        try { await host.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Trace.TraceWarning("Closing the phone link failed: {0}", ex); }

        _plan.Clear();
    }

    /// <summary>Keeps the link up for as long as the returned scope is alive — the panel, while it is
    /// open.</summary>
    internal IDisposable HoldOpen()
    {
        Interlocked.Increment(ref _holds);
        return new Hold(this);
    }

    /// <summary>Stops the link if nothing needs it: not asked to stay connected, no panel open, no phone
    /// paired.</summary>
    internal async Task StopIfUnneededAsync()
    {
        if (_disposed) return;

        try { await _lifecycle.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }

        var stopped = false;
        try
        {
            if (_disposed || _host is null || ShouldKeepRunning || Volatile.Read(ref _holds) > 0 || _host.Peers.Count > 0)
                return;

            await StopCoreAsync().ConfigureAwait(false);
            stopped = true;
        }
        finally
        {
            _lifecycle.Release();
        }

        if (stopped) StateChanged?.Invoke();
    }

    private void OnSettingsChanged()
    {
        var enabled = _settings.Settings.Phone.Enabled;
        if (enabled == _appliedEnabled) return;
        _appliedEnabled = enabled;

        _ = enabled ? StartAsync() : StopIfUnneededAsync();
    }

    /// <summary>
    /// Takes away what the Kestrel bridge left in this application's directory.
    /// </summary>
    /// <remarks>
    /// <c>bridge.pfx</c> holds a private key, and <c>sessions.json</c> the hashes of tokens that no longer
    /// open anything. Neither is read by anything now, so the key is the one thing worth not leaving
    /// behind. Called once at startup, whether or not the link ever runs. Best effort: a file that cannot be deleted is one nothing will ever read again either.
    /// </remarks>
    public static void ForgetKestrelLeftovers()
    {
        try
        {
            var directory = AppPaths.GetPhoneDirectory();
            foreach (var name in (string[])["bridge.pfx", "sessions.json", "tailscale.crt", "tailscale.key"])
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Removing the old phone bridge's files failed: {0}", ex.Message);
        }
    }

    private sealed class Hold(PhoneBridgeManager owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            Interlocked.Decrement(ref owner._holds);
            _ = owner.StopIfUnneededAsync();
        }
    }

    // ── pairing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mints an invitation for one phone and says where to send it.
    /// </summary>
    /// <remarks>The code rides in the URL's fragment, which a browser never sends to the server — so
    /// GitHub, which hosts the page, never sees it. Single use: a code photographed off the screen after
    /// the phone it was meant for has paired opens nothing.</remarks>
    internal async Task<PhoneInvitation> InviteAsync()
    {
        if (_host is not { } host)
            throw new InvalidOperationException("The phone link is not running.");

        if (host.Peers.Count >= MaxDevices)
            throw new PhoneBridgeException(
                $"{MaxDevices} devices are already paired. Unpair one before pairing another.");

        var invitation = await host.InviteAsync(new InvitationRequest
        {
            Label = "phone",
            Lifetime = CodeLifetime,
            SingleUse = true,
        }).ConfigureAwait(false);

        var code = invitation.Code.Value;
        var page = _settings.Settings.Phone.PageUrl;
        return new PhoneInvitation(invitation.Id, $"{page}#{Uri.EscapeDataString(code)}", code, invitation.ExpiresAt);
    }

    /// <summary>Withdraws a code that is no longer on screen.</summary>
    internal async Task RevokeAsync(Guid invitationId)
    {
        if (_host is not { } host) return;
        try { await host.RevokeInvitationAsync(invitationId).ConfigureAwait(false); }
        catch (Exception ex) { Trace.TraceWarning("Withdrawing a phone invitation failed: {0}", ex.Message); }
    }

    /// <summary>Unpairs a phone: it is dropped, forgotten, and refused if it comes back.</summary>
    internal Task UnpairAsync(PhoneDevice device) => UnpairAsync(device.Peer);

    private async Task UnpairAsync(ILinkPeer peer)
    {
        if (_host is not { } host) return;

        await host.ForgetPeerAsync(peer).ConfigureAwait(false);
        _plan.Remove(peer.Key);

        RememberWhetherPaired(host.Peers.Count > 0);
        StateChanged?.Invoke();
        await StopIfUnneededAsync().ConfigureAwait(false);
    }

    /// <summary>How long a phone that logged itself out is given to receive the answer before it is
    /// forgotten.</summary>
    internal static readonly TimeSpan UnpairGrace = TimeSpan.FromSeconds(1);

    private async Task ForgetAfterAnsweringAsync(ILinkPeer peer)
    {
        try
        {
            await Task.Delay(UnpairGrace).ConfigureAwait(false);
            _dispatcher.Post(() => _phoneDictation.CancelIfFrom(peer));
            await UnpairAsync(peer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Not awaited by anybody. The phone has already forgotten this machine, so what is left is a
            // row in the panel that Unpair there still removes.
            Trace.TraceWarning("Forgetting a phone that logged out failed: {0}", ex);
        }
    }

    private void OnPeerJoined(object? sender, PeerEventArgs e)
    {
        // Everything is sent again: a phone that reconnects has a page that may have been reloaded, and
        // anything it was sent before the drop is state it cannot be assumed to still hold.
        _plan.ForgetWhatWasSent(e.Peer.Key);

        RememberWhetherPaired(true);
        StateChanged?.Invoke();
    }

    private void OnPeerLeft(object? sender, PeerLeftEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            // A recording whose phone has gone will never be ended by it.
            _phoneDictation.CancelIfFrom(e.Peer);
        });
        StateChanged?.Invoke();
    }

    /// <summary>Written to settings, because it decides whether the link starts with the application —
    /// before there is a link to ask.</summary>
    private void RememberWhetherPaired(bool paired)
    {
        _dispatcher.Post(() =>
        {
            if (_settings.Settings.Phone.HasPairedDevices == paired) return;
            _settings.Settings.Phone.HasPairedDevices = paired;
            _settings.NotifyChanged();
        });
    }

    // ── requests ────────────────────────────────────────────────────────────────────────────────

    private async Task<ReadOnlyMemory<byte>> HandleRequestAsync(ILinkPeer peer, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (PhoneProtocol.Parse(body.Span) is not { } request)
            return PhoneProtocol.Error("mTiles did not understand that.");

        try
        {
            return await AnswerAsync(peer, request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An exception out of here reaches the phone as a failed handler and nothing else; logged
            // here, it at least says which request and why.
            Trace.TraceWarning("A phone request ({0}) failed: {1}", request.GetType().Name, ex);
            return PhoneProtocol.Error("mTiles could not do that.");
        }
    }

    private async Task<byte[]> AnswerAsync(ILinkPeer peer, PhoneRequest request)
    {
        switch (request)
        {
            case HelloRequest hello:
                var session = await _dispatcher.InvokeAsync(SessionSnapshot).ConfigureAwait(false);
                return PhoneProtocol.Ok(new
                {
                    ok = true,
                    protocol = PhoneProtocol.Version,
                    compatible = PhoneProtocol.Accepts(hello.Protocol),
                    app = "mTiles",
                    version = AppInfo.Version,
                    machine = Environment.MachineName,
                    // What a card's "changed at" is measured against: the phone's clock is not this one.
                    now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    session,
                });

            case UnpairRequest:
                // Answered first and forgotten a moment later: forgetting drops the session this answer
                // travels on, and a phone left without one cannot tell a logout from a lost connection.
                _ = ForgetAfterAnsweringAsync(peer);
                return PhoneProtocol.Ok();

            case WorkspacesRequest:
                return PhoneProtocol.Ok(new { ok = true, workspaces = await _dispatcher.InvokeAsync(_workspaces.List).ConfigureAwait(false) });

            case LayoutRequest layout:
                return await _dispatcher.InvokeAsync(() => _workspaces.Layout(layout.WorkspaceId)).ConfigureAwait(false) is { } found
                    ? PhoneProtocol.Ok(new { ok = true, layout = found })
                    : PhoneProtocol.Error("That workspace is not open in mTiles.");

            case OpenWorkspaceRequest open:
                return await _dispatcher.InvokeAsync(() => _workspaces.Open(open.WorkspaceId)).ConfigureAwait(false)
                    ? PhoneProtocol.Ok()
                    : PhoneProtocol.Error("That workspace is no longer in mTiles.");

            case WatchRequest watch:
                _plan.Watch(peer.Key, watch.WorkspaceId, watch.TileId);
                _ = PushNowAsync();
                return PhoneProtocol.Ok();

            case TileRequest tile:
                return await _dispatcher.InvokeAsync(() => _workspaces.Find(tile.TileId) is { } hit
                        ? PhoneTiles.Describe(hit.Tile, hit.WorkspaceId)
                        : null).ConfigureAwait(false) is { } view
                    ? PhoneProtocol.Ok(new { ok = true, tile = view })
                    : PhoneProtocol.Error("That tile is no longer there.");

            case ConversationsRequest list:
                var (conversations, problem) = await ListConversationsAsync(list.TileId).ConfigureAwait(false);
                return conversations is not null
                    ? PhoneProtocol.Ok(new { ok = true, conversations })
                    : PhoneProtocol.Error(problem ?? "mTiles could not list the conversations.");

            case TileCommandRequest command:
                return await RunTileCommandAsync(command).ConfigureAwait(false) is { } refusal
                    ? PhoneProtocol.Refusal(refusal)
                    : PhoneProtocol.Ok();

            case ActionRequest action:
                return await StartActionAsync(peer, action).ConfigureAwait(false) is { } why
                    ? PhoneProtocol.Error(why)
                    : PhoneProtocol.Ok();

            default:
                return PhoneProtocol.Error("mTiles did not understand that.");
        }
    }

    private Task<RemoteRefusal?> RunTileCommandAsync(TileCommandRequest request) =>
        OnTileAsync<RemoteRefusal?>(request.TileId, "That tile is no longer there.", async tile =>
        {
            var result = await PhoneTiles.HandleAsync(tile, request.Command).ConfigureAwait(true);
            _ = PushNowAsync();
            return result;
        }, failure: "mTiles could not do that.");

    /// <summary>The tile's conversations, or the sentence saying why there are none to show: a missing tile,
    /// a tile holding no conversations and a list that could not be read are three different answers.</summary>
    private Task<ConversationsAnswer> ListConversationsAsync(string tileId) =>
        OnTileAsync(tileId, ConversationsAnswer.Failed("That tile is no longer there."), async tile =>
            tile.Content is IRemoteConversationsTile conversations
                ? new ConversationsAnswer(await conversations.ConversationsForRemoteAsync().ConfigureAwait(true), null)
                : ConversationsAnswer.Failed("That tile holds no conversations."),
            failure: ConversationsAnswer.Failed("mTiles could not list the conversations."));

    private sealed record ConversationsAnswer(IReadOnlyList<RemoteConversation>? Conversations, string? Problem)
    {
        public static ConversationsAnswer Failed(string problem) => new(null, problem);
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread against the tile a phone named.</summary>
    /// <remarks>
    /// Wrapped: this runs the tile's own code, synchronously on this thread — a key press ends in a
    /// RaiseEvent through the application's own handlers — and a throw from any of it must cost the
    /// phone a sentence, not the request.
    /// </remarks>
    private Task<T> OnTileAsync<T>(string tileId, T whenGone, Func<LeafTileNodeViewModel, Task<T>> work, T failure) =>
        _dispatcher.InvokeAsync(async () =>
        {
            if (_workspaces.Find(tileId) is not { } hit)
                return whenGone;
            try
            {
                return await work(hit.Tile).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("A phone request in a tile failed: {0}", ex);
                return failure;
            }
        }).Unwrap();

    /// <summary>
    /// Starts a tile action and answers as soon as it has been checked, not when it has finished.
    /// </summary>
    /// <remarks>
    /// A tile action is the one thing a phone can ask for that is not short: Continue on a Goal tile runs
    /// the whole implement/review loop, minutes of it, and a request waiting on that would time out on the
    /// phone long before. What the phone needs to know at once is whether it was allowed; how it ended is
    /// pushed to it afterwards if it failed. The id is checked against what the tile offers <em>now</em>
    /// (<see cref="PhoneTileActions.IsAllowed"/>), so an action already running is refused for being
    /// disabled rather than queued behind itself.
    /// </remarks>
    private Task<string?> StartActionAsync(ILinkPeer peer, ActionRequest request) =>
        _dispatcher.InvokeAsync<string?>(() =>
        {
            if (_workspaces.Find(request.TileId) is not { } hit)
                return "That tile is no longer there.";

            if (!PhoneTileActions.IsAllowed(hit.Tile.Actions, request.ActionId))
                return "That is not something this tile can do right now.";

            _ = RunActionAsync(peer, hit.Tile, request.ActionId);
            return null;
        });

    private async Task RunActionAsync(ILinkPeer peer, LeafTileNodeViewModel tile, string actionId)
    {
        string? why;
        try
        {
            var result = await tile.InvokeActionAsync(actionId).ConfigureAwait(true);
            why = result.Done ? null : result.Message ?? "mTiles could not do that.";
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Running a tile action from a phone failed: {0}", ex);
            why = "mTiles could not do that.";
        }

        if (why is not null)
            await NotifyAsync(peer, PhoneProtocol.Push("error", new { scope = "action", message = why })).ConfigureAwait(false);
        _ = PushNowAsync();
    }


    /// <summary>What every phone is told about the application as a whole. UI thread only.</summary>
    private object SessionSnapshot()
    {
        return new
        {
            dictation = _phoneDictation.Snapshot(),
            activeTileId = SafeActiveTileId(),
        };
    }

    private void OnDictationChanged() => _ = PushNowAsync();

    private string? SafeActiveTileId()
    {
        try { return _workspaces.ActiveTile?.TileId; }
        catch { return null; }
    }

    // ── pushing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Samples what each connected phone is looking at and sends whatever has changed since it was last
    /// told.
    /// </summary>
    /// <remarks>
    /// <para>Sampled, not driven by events, and that is the simpler of two correct designs rather than
    /// the lazy one: what a phone shows is drawn from half a dozen view models in several kinds of tile,
    /// and a missed notification anywhere in them is a phone showing yesterday's state with nothing to say
    /// so. A tile that implements <see cref="IRemoteViewTile"/> says cheaply whether it moved, so the
    /// expensive part — describing a long conversation — happens only when it did.</para>
    /// <para>Only while a phone is connected, and one sampling at a time.</para>
    /// </remarks>
    internal async Task PushNowAsync()
    {
        if (_disposed || _host is not { } host) return;
        if (Interlocked.Exchange(ref _pushing, 1) == 1)
        {
            Volatile.Write(ref _pushRequested, 1);
            return;
        }

        try
        {
            // This round covers everything asked for up to now.
            Volatile.Write(ref _pushRequested, 0);

            var up = host.Peers.Where(p => p.IsConnected).ToList();
            var connected = up.Where(p => !_sending.ContainsKey(p.Key)).ToList();
            if (connected.Count < up.Count) Volatile.Write(ref _pushOwedAfterSend, 1);
            if (connected.Count == 0) return;

            var tick = Interlocked.Increment(ref _tick);
            var outgoing = await _dispatcher.InvokeAsync(() =>
                _plan.Collect(connected, PhoneProtocol.Push("session", SessionSnapshot()), full: tick % 4 == 1))
                .ConfigureAwait(false);

            // Collecting is one at a time; sending is not awaited here, so a phone whose relay has stopped
            // answering waits out its own timeouts without holding the next sampling — or any other phone.
            foreach (var o in outgoing)
                if (o.Messages.Count > 0 && _sending.TryAdd(o.Peer.Key, 0))
                    _ = SendInOrderAsync(o.Peer, o.Messages);
        }
        catch (Exception ex)
        {
            // From a timer: an escape here ends the process.
            Trace.TraceWarning("Updating a phone failed: {0}", ex.Message);
        }
        finally
        {
            Volatile.Write(ref _pushing, 0);
        }

        if (Interlocked.Exchange(ref _pushRequested, 0) == 1)
            _ = PushNowAsync();
    }

    private async Task SendInOrderAsync(ILinkPeer peer, IReadOnlyList<byte[]> messages)
    {
        try
        {
            foreach (var message in messages)
            {
                if (await NotifyAsync(peer, message).ConfigureAwait(false)) continue;

                // The plan recorded these as sent when it collected them; one that did not arrive must be
                // offered again, or the phone keeps showing what it had until the tile next changes.
                _plan.ForgetWhatWasSent(peer.Key);
                return;
            }
        }
        catch (Exception ex)
        {
            // Not awaited by anybody: an escape here would be an unobserved task.
            Trace.TraceWarning("Updating a phone failed: {0}", ex.Message);
        }
        finally
        {
            _sending.TryRemove(peer.Key, out _);
            // A sampling that left this phone out — a watch it asked for meanwhile, a change in the tile —
            // is owed now rather than at the next tick.
            if (Interlocked.Exchange(ref _pushOwedAfterSend, 0) == 1)
                _ = PushNowAsync();
        }
    }

    /// <summary>True when the message was delivered.</summary>
    internal static async Task<bool> NotifyAsync(ILinkPeer peer, byte[] message)
    {
        if (!peer.IsConnected) return false;

        // Bounded: a notification waits through a dropped session by design, and a phone that has gone
        // for good must not hold the push loop behind it.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await peer.NotifyAsync(message, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is LinkException or OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        _phoneDictation.Changed -= OnDictationChanged;
        _phoneDictation.Dispose();
        _settings.SettingsChanged -= OnSettingsChanged;

        // Not an unpairing: closing the application is not the user deciding to forget their phone.
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }

        // The semaphore is deliberately not disposed: a call that was already waiting on it enters after
        // this, sees _disposed and releases it, and that release must not throw from an abandoned task.
    }
}

/// <summary>One paired phone, as the panel lists it.</summary>
internal sealed record PhoneDevice(ILinkPeer Peer, string Name, bool IsConnected, DateTimeOffset PairedAt,
    DateTimeOffset LastSeen);

/// <summary>A code on screen: what the QR code carries, and until when.</summary>
internal sealed record PhoneInvitation(Guid Id, string Url, string Code, DateTimeOffset ExpiresAt);

/// <summary>A refusal worth showing as it stands.</summary>
internal sealed class PhoneBridgeException(string message) : Exception(message);
