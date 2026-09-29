using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Speech;
using mTiles.ViewModels;
using Tailcat.Link;

namespace mTiles.Services.Phone;

/// <summary>
/// The route from a phone's microphone into a tile: one utterance per audio channel, who owns the
/// recording in flight, and what the phones are told about dictation.
/// </summary>
/// <remarks>Split out of <see cref="PhoneBridgeManager"/>, which keeps the link, the pairing and the
/// pushing; the rules for when a phone may dictate, and where its words land, change on their own.</remarks>
internal sealed class PhoneDictation : IDisposable
{
    private readonly SettingsService _settings;
    private readonly DictationService _dictation;
    private readonly RoutedAudioCapture _router;
    private readonly IPhoneWorkspaces _workspaces;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>The phone whose recording is in flight. Written on the UI thread and read on the link's
    /// thread for every audio frame, hence volatile: a recording cancelled on the desktop must stop
    /// taking that phone's frames at once.</summary>
    private volatile ILinkPeer? _streamOwner;

    /// <summary>The tile the recording's words are going to. UI thread only.</summary>
    private LeafTileNodeViewModel? _streamTile;

    internal PhoneDictation(SettingsService settings, DictationService dictation, RoutedAudioCapture router,
        IPhoneWorkspaces workspaces, IUiDispatcher dispatcher)
    {
        _settings = settings;
        _dictation = dictation;
        _router = router;
        _workspaces = workspaces;
        _dispatcher = dispatcher;
        _dictation.StateChanged += OnDictationStateChanged;
    }

    /// <summary>Raised when the dictation's state moves, so the phones can be told. On any thread.</summary>
    public event Action? Changed;

    /// <summary>What has the keyboard right now; see <see cref="PhoneBridgeManager.FocusedElement"/>.</summary>
    internal Func<Avalonia.Input.IInputElement?>? FocusedElement { get; set; }

    /// <summary>Drops the recording in flight when it is this phone's. UI thread only.</summary>
    internal void CancelIfFrom(ILinkPeer peer)
    {
        if (IsRecordingFrom(peer)) CancelRecording();
    }

    /// <summary>What every phone is told about dictation. UI thread only.</summary>
    internal object Snapshot()
    {
        var ready = _settings.Settings.Speech.Enabled
                    && _dictation.SelectedModel is { } model && _dictation.Store.IsDownloaded(model);
        return new
        {
            available = ready,
            state = _dictation.State switch
            {
                DictationState.Recording => "recording",
                DictationState.Transcribing => "transcribing",
                _ => "idle",
            },
            tileId = _streamTile?.TileId,
        };
    }

    public void Dispose() => _dictation.StateChanged -= OnDictationStateChanged;

    /// <summary>One utterance, from its header frame to the channel closing. See
    /// <see cref="PhoneProtocol.AudioChannel"/>.</summary>
    internal async Task HandleAudioAsync(ILinkPeer peer, ILinkChannelReader channel, CancellationToken cancellationToken)
    {
        ChannelCloseReason? closed = null;
        channel.Closed += (_, e) => closed = e.Reason;

        var began = false;
        var cancelled = false;
        try
        {
            await foreach (var frame in channel.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!began)
                {
                    var refusal = PhoneProtocol.ParseAudioHeader(frame.Span) is { } header
                        ? await BeginRecordingAsync(peer, header).ConfigureAwait(false)
                        : "mTiles did not understand that recording.";

                    if (refusal is not null)
                    {
                        await PhoneBridgeManager.NotifyAsync(peer, PhoneProtocol.Push("error", new { scope = "dictation", message = refusal }))
                            .ConfigureAwait(false);
                        return;
                    }

                    began = true;
                    continue;
                }

                if (frame.Length == 1 && frame.Span[0] == PhoneProtocol.CancelMarker)
                {
                    cancelled = true;
                    break;
                }

                // Straight into the capture, off the UI thread: this is the hot path, and the capture has
                // its own lock. Only while this phone still owns the recording — a cancel from the desktop
                // must not be undone by frames still in flight.
                if (IsRecordingFrom(peer))
                    _router.Phone.Write(frame.Span);
            }
        }
        finally
        {
            if (began)
            {
                // Only a channel closed on purpose is the end of a sentence. One that ended with the session
                // is half a sentence, and half a command typed into a terminal is worse than none.
                if (!cancelled && closed == ChannelCloseReason.PeerClosed)
                    _dispatcher.Post(() => EndRecording(peer));
                else
                    _dispatcher.Post(() => { if (IsRecordingFrom(peer)) CancelRecording(); });
            }
        }
    }

    /// <summary>
    /// A sentence meant for the phone's box whose push did not arrive is typed into the tile instead —
    /// without Enter, so it waits there to be read — rather than being lost with nothing said anywhere.
    /// </summary>
    private async Task KeepIfUndeliveredAsync(Task<bool> pushed, LeafTileNodeViewModel? tile, string text)
    {
        if (await pushed.ConfigureAwait(false)) return;
        Trace.TraceWarning("A dictated sentence could not be handed back to the phone; typing it into the tile instead.");
        _dispatcher.Post(() =>
        {
            var delivery = PhoneDelivery(_settings.Settings);
            delivery.AutoSubmitEnter = false;
            DictationTextSink.Insert(tile, text, delivery, null);
        });
    }

    private bool IsRecordingFrom(ILinkPeer peer) => _streamOwner is { } owner && owner.Key == peer.Key;

    private Task<string?> BeginRecordingAsync(ILinkPeer peer, AudioHeader header) =>
        _dispatcher.InvokeAsync<string?>(() =>
        {
            // Each refusal names its own cause. The phone is often the only screen the user is looking at.
            if (!_settings.Settings.Speech.Enabled)
                return "Dictation is switched off in mTiles.";

            // Deliberately not DictationService.IsReady, which also asks whether this machine has a
            // microphone — and the machines with none, the far end of a remote desktop, are the ones this
            // exists for.
            if (_dictation.SelectedModel is not { } model || !_dictation.Store.IsDownloaded(model))
                return "mTiles has no speech model downloaded yet. Set up dictation on the computer first.";

            if (_dictation.State == DictationState.Transcribing)
                return "mTiles is still working out the previous recording.";

            if (_dictation.State != DictationState.Idle)
                return "mTiles is already recording.";

            // A phone zoomed into a tile is aiming at that tile, whatever has the focus on the desktop. One
            // that has not chosen dictates the way the shortcut does.
            LeafTileNodeViewModel? tile;
            Avalonia.Input.IInputElement? focused = null;
            if (header.TileId is { } tileId)
            {
                if (_workspaces.Find(tileId) is not { } hit)
                    return "That tile is no longer there.";
                tile = hit.Tile;
            }
            else
            {
                tile = _workspaces.ActiveTile;
                focused = SafeFocusedElement();
            }

            var forPhone = PhoneDelivery(_settings.Settings);
            bool started;
            try
            {
                // Prepared before the route is armed: frames follow the header without waiting for a reply.
                _router.Phone.PrepareForStream(header.SampleRate);
                _router.RouteNextToPhone();

                var toDraft = header.ToDraft;
                started = _dictation.Start(tile ?? (object)"phone", text =>
                {
                    // To the phone that spoke, and only that one: a second paired device has no business
                    // showing somebody else's sentence.
                    var route = SentenceRoute.For(toDraft, forPhone.AutoSubmitEnter);
                    var pushed = PhoneBridgeManager.NotifyAsync(peer, PhoneProtocol.Push("text",
                        new { message = text, tileId = tile?.TileId, draft = route.Draft, send = route.Send }));
                    if (!route.TypedIntoTile) _ = KeepIfUndeliveredAsync(pushed, tile, text);
                    return !route.TypedIntoTile || DictationTextSink.Insert(tile, text, forPhone, focused);
                });
            }
            catch (Exception ex)
            {
                // The armed route must not survive: it is a one-shot flag the next Start consumes, and left
                // set it sends the user's own microphone press to an empty phone capture.
                _router.CancelPhoneRoute();
                Trace.TraceWarning("Starting a phone dictation failed: {0}", ex);
                return "Dictation could not be started on the computer.";
            }

            if (!started)
            {
                _router.CancelPhoneRoute();
                return "Dictation could not be started on the computer.";
            }

            _streamOwner = peer;
            _streamTile = tile;
            return null;
        });

    private void EndRecording(ILinkPeer peer)
    {
        if (!IsRecordingFrom(peer)) return;

        if (_dictation.State == DictationState.Recording && _router.IsRecordingFromPhone)
            _dictation.Stop();
    }

    private void CancelRecording()
    {
        if (_dictation.State == DictationState.Recording && _router.IsRecordingFromPhone)
            _dictation.Cancel();

        _router.CancelPhoneRoute();
        _streamOwner = null;
        _streamTile = null;
    }

    private void OnDictationStateChanged()
    {
        _dispatcher.Post(() =>
        {
            // Let go of the phone once the utterance is over, so the next recording from any device is
            // judged afresh.
            if (_dictation.State == DictationState.Idle)
            {
                _streamOwner = null;
                _streamTile = null;
            }
        });
        Changed?.Invoke();
    }

    /// <summary>
    /// The settings a phone-driven transcript is composed with.
    /// </summary>
    /// <remarks>A copy carrying only what <see cref="DictationTextSink.Compose"/> reads, so the phone's
    /// own auto-Enter preference applies without the stored keyboard settings being mutated.</remarks>
    internal static SpeechSettings PhoneDelivery(AppSettings settings) => new()
    {
        AutoSubmitEnter = settings.Phone.AutoSubmitEnter,
        AppendTrailingSpace = settings.Speech.AppendTrailingSpace,
    };

    private Avalonia.Input.IInputElement? SafeFocusedElement()
    {
        try { return FocusedElement?.Invoke(); }
        catch { return null; }
    }

    /// <summary>Where a phone's sentence goes. Asked for in the phone's own box, it is read and edited there
    /// and sent from there — typed into nothing here, delivered by being handed back, and sent at once only
    /// when the phone's auto-Enter in Settings says so. Otherwise it is typed into the tile, the older
    /// page's route. The two flags are <c>null</c> rather than false so an older page sees no new keys.</summary>
    internal readonly record struct SentenceRoute(bool? Draft, bool? Send, bool TypedIntoTile)
    {
        internal static SentenceRoute For(bool toDraft, bool autoEnter) => toDraft
            ? new SentenceRoute(true, autoEnter ? true : null, TypedIntoTile: false)
            : new SentenceRoute(null, null, TypedIntoTile: true);
    }
}
