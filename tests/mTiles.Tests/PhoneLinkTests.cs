using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using mTiles.Models;
using mTiles.Services.Phone;
using mTiles.Services.Phone.Remote;
using mTiles.Services.Speech;
using mTiles.ViewModels;
using Tailcat.Link;
using Tailcat.Link.Storage;
using Tailcat.TestSupport;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The bridge end to end: a phone pairs over a relay, asks, is answered, is pushed to, and is refused.
/// </summary>
/// <remarks>
/// Over <see cref="FakeDerpRelay"/>, an in-memory relay, with the phone played by the library's own
/// .NET client — the same protocol the browser client speaks, and the same host code the application
/// runs. What stands in for the window is <see cref="FakeWorkspaces"/>: two tiles in one workspace.
/// </remarks>
[Trait("Category", "Slow")] // two real link nodes handshaking over loopback; a second or two each
public sealed class PhoneLinkTests : IAsyncLifetime
{
    private readonly TempSettings _settings = new();
    private readonly FakeDerpRelay _relay = new();
    private readonly FakeWorkspaces _workspaces = new();
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
    private FakeRelayGatewayFactory _gateways = null!;
    private PhoneBridgeManager _manager = null!;
    private readonly List<ILink> _phones = [];
    private readonly TempDirectory _models = new("mtiles-phone-models");
    private readonly FakeSpeechEngine _engine = new() { Transcript = "from the phone" };
    private DictationService _dictation = null!;

    public Task InitializeAsync()
    {
        _gateways = new FakeRelayGatewayFactory(_relay);
        var router = new RoutedAudioCapture(new IdleMicrophone(), new PhoneAudioCapture());
        _dictation = new DictationService(_settings.Service, router, _engine, new SpeechModelStore(_models.Path),
            action => action(), deliberateHold: TimeSpan.Zero);
        _manager = new PhoneBridgeManager(
            _settings.Service,
            _dictation,
            router,
            _workspaces,
            new InlineUiDispatcher(),
            () => Options(new InMemoryLinkStore()) with { MaxPeers = PhoneBridgeManager.MaxDevices },
            pushInterval: TimeSpan.Zero);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var phone in _phones)
            await phone.DisposeAsync();
        await _manager.DisposeAsync();
        await _relay.DisposeAsync();
        _deadline.Dispose();
        _settings.Dispose();
        _models.Dispose();
    }

    private CancellationToken Ct => _deadline.Token;

    private LinkOptions Options(ILinkStore store) => new()
    {
        Store = store,
        Gateway = _gateways,
        RequestTimeout = TimeSpan.FromSeconds(5),
        RequestDeadline = TimeSpan.FromSeconds(30),
        HeartbeatInterval = TimeSpan.FromSeconds(1),
        MinReconnectDelay = TimeSpan.FromMilliseconds(200),
        MaxReconnectDelay = TimeSpan.FromSeconds(2),
    };

    /// <summary>A phone that has scanned the code on screen, with every push it receives kept.</summary>
    private async Task<(ILink Phone, ConcurrentQueue<JsonElement> Pushes)> PairAsync()
    {
        Assert.True(await _manager.StartAsync(), _manager.LastError);
        var invitation = await _manager.InviteAsync();

        // The code rides in the page URL's fragment, escaped; the page reads it back out.
        Assert.StartsWith(PhoneSettings.DefaultPageUrl + "#", invitation.Url);
        Assert.Equal(invitation.Code, Uri.UnescapeDataString(invitation.Url[(invitation.Url.IndexOf('#') + 1)..]));

        var phone = await TailcatLink.JoinAsync(PhoneProtocol.AppName, invitation.Code,
            Options(new InMemoryLinkStore()), Ct);
        _phones.Add(phone);

        var pushes = new ConcurrentQueue<JsonElement>();
        phone.OnRequest((request, _) =>
        {
            pushes.Enqueue(JsonDocument.Parse(request.ToArray()).RootElement.Clone());
            return Task.FromResult<ReadOnlyMemory<byte>>(Array.Empty<byte>());
        });

        await phone.WaitUntilConnectedAsync(Ct);
        await WaitUntil(() => _manager.ConnectedDevices == 1, "the phone is connected");
        return (phone, pushes);
    }

    private async Task<JsonElement> Ask(ILink phone, string json)
    {
        var answer = await phone.RequestAsync(json, Ct);
        return JsonDocument.Parse(answer).RootElement.Clone();
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) Assert.Fail($"Timed out waiting until {what}.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_paired_phone_sees_the_workspaces_their_layout_and_a_tile()
    {
        var (phone, _) = await PairAsync();

        var hello = await Ask(phone, $$"""{"type":"hello","protocol":{{PhoneProtocol.Version}}}""");
        Assert.True(hello.GetProperty("ok").GetBoolean());
        Assert.True(hello.GetProperty("compatible").GetBoolean());

        var list = await Ask(phone, """{"type":"workspaces"}""");
        Assert.Equal("ws", list.GetProperty("workspaces")[0].GetProperty("id").GetString());

        var layout = await Ask(phone, """{"type":"layout","workspaceId":"ws"}""");
        var root = layout.GetProperty("layout").GetProperty("root");
        Assert.Equal("split", root.GetProperty("type").GetString());
        Assert.Equal("chat", root.GetProperty("first").GetProperty("tileId").GetString());

        var tile = await Ask(phone, """{"type":"tile","tileId":"chat"}""");
        Assert.Equal("chat", tile.GetProperty("tile").GetProperty("view").GetString());
        Assert.Equal("hello from the tile", tile.GetProperty("tile").GetProperty("chat")
            .GetProperty("items")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_command_reaches_the_tile_it_names_and_no_other()
    {
        var (phone, _) = await PairAsync();

        var sent = await Ask(phone, """{"type":"send","tileId":"chat","text":"ship it","submit":true}""");
        Assert.True(sent.GetProperty("ok").GetBoolean());
        Assert.Equal([new RemoteSendText("ship it", true)], _workspaces.Chat.Received);
        Assert.Empty(_workspaces.Other.Received);

        // The tile's own refusal comes back as its own sentence.
        var refused = await Ask(phone, """{"type":"interrupt","tileId":"chat"}""");
        Assert.Equal("Nothing to stop.", refused.GetProperty("error").GetString());

        var gone = await Ask(phone, """{"type":"send","tileId":"nowhere","text":"x"}""");
        Assert.Equal("That tile is no longer there.", gone.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Nonsense_is_answered_with_an_error_and_the_link_carries_on()
    {
        var (phone, _) = await PairAsync();

        var answer = await Ask(phone, """{"type":"key","tileId":"chat","key":123}""");
        Assert.False(answer.GetProperty("ok").GetBoolean());

        Assert.True((await Ask(phone, """{"type":"workspaces"}""")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task An_action_a_phone_may_not_press_is_refused()
    {
        var (phone, _) = await PairAsync();

        var restart = await Ask(phone, """{"type":"action","tileId":"chat","id":"restart"}""");
        Assert.Equal("That is not something this tile can do right now.", restart.GetProperty("error").GetString());

        var ok = await Ask(phone, """{"type":"action","tileId":"chat","id":"continue"}""");
        Assert.True(ok.GetProperty("ok").GetBoolean());
        await WaitUntil(() => _workspaces.Chat.Invoked.Contains("continue"), "the action has run");
        Assert.DoesNotContain("restart", _workspaces.Chat.Invoked);
    }

    /// <summary>What a phone watches is pushed to it — once, and again only when it has moved.</summary>
    [Fact]
    public async Task A_watched_tile_is_pushed_when_it_changes_and_only_then()
    {
        var (phone, pushes) = await PairAsync();

        await Ask(phone, """{"type":"watch","workspaceId":"ws","tileId":"chat"}""");
        await _manager.PushNowAsync();
        await WaitUntil(() => pushes.Any(p => Type(p) == "tile"), "the tile has been pushed");
        await WaitUntil(() => pushes.Any(p => Type(p) == "layout"), "the layout has been pushed");

        var before = pushes.Count(p => Type(p) == "tile");
        await _manager.PushNowAsync();
        await _manager.PushNowAsync();
        await Task.Delay(200);
        Assert.Equal(before, pushes.Count(p => Type(p) == "tile"));

        _workspaces.Chat.Say("a new line");
        await _manager.PushNowAsync();
        await WaitUntil(() => pushes.Count(p => Type(p) == "tile") > before, "the change has been pushed");
        Assert.Contains(pushes, p => Type(p) == "tile" && p.ToString().Contains("a new line"));
    }

    /// <summary>A recording nobody can transcribe is refused in words, over the link, and nothing is
    /// started.</summary>
    [Fact]
    public async Task A_recording_is_refused_while_dictation_is_off()
    {
        _settings.Service.Settings.Speech.Enabled = false;
        var (phone, pushes) = await PairAsync();

        await using (var audio = await phone.OpenChannelAsync(PhoneProtocol.AudioChannel, Ct))
            await audio.SendAsync("""{"tileId":"chat","sampleRate":48000}"""u8.ToArray(), Ct);

        await WaitUntil(() => pushes.Any(p => Type(p) == "error"), "the refusal has arrived");
        var error = pushes.First(p => Type(p) == "error");
        Assert.Equal("dictation", error.GetProperty("scope").GetString());
        Assert.Equal("Dictation is switched off in mTiles.", error.GetProperty("message").GetString());
    }

    /// <summary>A recording the phone closes on purpose is a sentence: it is transcribed and the text comes
    /// back to the phone that spoke.</summary>
    [Fact]
    public async Task A_recording_closed_by_the_phone_is_transcribed()
    {
        EnableDictation();
        var (phone, pushes) = await PairAsync();

        await using (var audio = await phone.OpenChannelAsync(PhoneProtocol.AudioChannel, Ct))
        {
            await audio.SendAsync("""{"tileId":"chat","sampleRate":16000}"""u8.ToArray(), Ct);
            await audio.SendAsync(new byte[3200], Ct);
            await WaitUntil(() => _dictation.State == DictationState.Recording, "the recording has started");
        }

        await WaitUntil(() => pushes.Any(p => Type(p) == "text"), "the transcript has come back");
        Assert.Equal(1, _engine.Calls);
    }

    /// <summary>A file the phone sends and closes on purpose reaches the tile whole, and its marker comes back
    /// to the phone rather than being typed into the tile.</summary>
    [Fact]
    public async Task An_attachment_closed_by_the_phone_is_answered_with_its_marker()
    {
        var (phone, pushes) = await PairAsync();

        await using (var attach = await phone.OpenChannelAsync(PhoneProtocol.AttachChannel, Ct))
        {
            await attach.SendAsync("""{"id":"u1","tileId":"chat","name":"a.png","mime":"image/png"}"""u8.ToArray(), Ct);
            await attach.SendAsync(new byte[1000], Ct);
            await attach.SendAsync(new byte[500], Ct);
        }

        await WaitUntil(() => pushes.Any(p => Type(p) == "attached"), "the answer has arrived");
        var answer = pushes.First(p => Type(p) == "attached");
        Assert.Equal("u1", answer.GetProperty("id").GetString());
        Assert.Equal("[Image #1]", answer.GetProperty("marker").GetString());
        Assert.Equal(("a.png", 1500), Assert.Single(_workspaces.Chat.Attached));
    }

    /// <summary>A tile that takes no attachments says so, rather than the phone waiting out its timeout.</summary>
    [Fact]
    public async Task An_attachment_for_a_tile_that_takes_none_is_refused_in_words()
    {
        var (phone, pushes) = await PairAsync();

        await using (var attach = await phone.OpenChannelAsync(PhoneProtocol.AttachChannel, Ct))
        {
            await attach.SendAsync("""{"id":"u2","tileId":"other","name":"a.txt"}"""u8.ToArray(), Ct);
            await attach.SendAsync(new byte[10], Ct);
        }

        await WaitUntil(() => pushes.Any(p => Type(p) == "attached"), "the refusal has arrived");
        var answer = pushes.First(p => Type(p) == "attached");
        Assert.Equal("That tile takes no attachments.", answer.GetProperty("error").GetString());
        Assert.Empty(_workspaces.Chat.Attached);
    }

    /// <summary>A recording whose session drops is half a sentence, and nothing is transcribed.</summary>
    [Fact]
    public async Task A_recording_cut_off_with_the_session_is_cancelled()
    {
        EnableDictation();
        var (phone, _) = await PairAsync();

        var audio = await phone.OpenChannelAsync(PhoneProtocol.AudioChannel, Ct);
        await audio.SendAsync("""{"tileId":"chat","sampleRate":16000}"""u8.ToArray(), Ct);
        await audio.SendAsync(new byte[3200], Ct);
        await WaitUntil(() => _dictation.State == DictationState.Recording, "the recording has started");

        _phones.Remove(phone);
        await phone.DisposeAsync();

        await WaitUntil(() => _dictation.State == DictationState.Idle, "the recording has been dropped");
        Assert.Equal(0, _engine.Calls);
    }

    private void EnableDictation()
    {
        var speech = _settings.Service.Settings.Speech;
        speech.Enabled = true;
        speech.ModelId = SpeechModelFiles.PlaceOnDisk(_models.Path).Id;
    }

    /// <summary>An unpaired phone is forgotten, and the bridge remembers there is nobody left.</summary>
    [Fact]
    public async Task Unpairing_forgets_the_phone()
    {
        await PairAsync();
        await WaitUntil(() => _settings.Service.Settings.Phone.HasPairedDevices, "the pairing is remembered");

        await _manager.UnpairAsync(Assert.Single(_manager.Devices));

        Assert.Empty(_manager.Devices);
        Assert.False(_settings.Service.Settings.Phone.HasPairedDevices);
    }

    /// <summary>A phone that logs itself out is told yes, and then forgotten here as Unpair would.</summary>
    [Fact]
    public async Task A_phone_can_log_itself_out()
    {
        var (phone, _) = await PairAsync();
        await WaitUntil(() => _settings.Service.Settings.Phone.HasPairedDevices, "the pairing is remembered");

        var answer = await Ask(phone, """{"type":"unpair"}""");

        Assert.True(answer.GetProperty("ok").GetBoolean());
        await WaitUntil(() => _manager.Devices.Count == 0, "the phone is forgotten");
        await WaitUntil(() => !_settings.Service.Settings.Phone.HasPairedDevices, "nobody is left paired");
    }

    private static string? Type(JsonElement push) =>
        push.TryGetProperty("type", out var t) ? t.GetString() : null;

    /// <summary>One workspace, two tiles side by side: a chat that records what it is asked, and a
    /// second tile that must never hear about it.</summary>
    private sealed class FakeWorkspaces : IPhoneWorkspaces
    {
        public readonly AttachingTile Chat = new("chat");
        public readonly StubTile Other = new("other");
        private readonly LeafTileNodeViewModel _chat;
        private readonly LeafTileNodeViewModel _other;
        private readonly SplitTileNodeViewModel _root;

        public FakeWorkspaces()
        {
            var scope = new TileActivationScope();
            _chat = new LeafTileNodeViewModel("agent-conversation", Chat, "", scope) { TileId = "chat", TileName = "Agent#1" };
            _other = new LeafTileNodeViewModel("agent-conversation", Other, "", scope) { TileId = "other", TileName = "Agent#2" };
            _root = new SplitTileNodeViewModel(Avalonia.Layout.Orientation.Vertical, _chat, _other);
        }

        public IReadOnlyList<RemoteWorkspace> List() =>
            [new RemoteWorkspace("ws", "project", "main", "idle", Loaded: true, Current: true, Favorite: false)];

        public RemoteLayout? Layout(string workspaceId) =>
            workspaceId == "ws" ? new RemoteLayout("ws", "project", LayoutProjection.Project(_root, PhoneTiles.IsReachable)) : null;

        public bool Open(string workspaceId) => workspaceId == "ws";

        public (LeafTileNodeViewModel Tile, string WorkspaceId)? Find(string tileId) => tileId switch
        {
            "chat" => (_chat, "ws"),
            "other" => (_other, "ws"),
            _ => null,
        };

        public LeafTileNodeViewModel? ActiveTile => _chat;
    }

    /// <summary>A chat that takes a photo or a file, and answers with a marker naming how many it holds.</summary>
    private sealed class AttachingTile(string name) : StubTile(name), IRemoteAttachTile
    {
        public ConcurrentQueue<(string Name, int Length)> Attached { get; } = new();

        public Task<RemoteAttachResult> AttachFromRemoteAsync(string name, string mimeType, byte[] data)
        {
            Attached.Enqueue((name, data.Length));
            return Task.FromResult(new RemoteAttachResult($"[Image #{Attached.Count}]"));
        }
    }

    private class StubTile(string name) : IRemoteViewTile, ITileActions
    {
        private readonly List<string> _lines = ["hello from the tile"];
        private long _version;

        public ConcurrentQueue<RemoteTileCommand> ReceivedQueue { get; } = new();
        public IReadOnlyList<RemoteTileCommand> Received => [.. ReceivedQueue];
        public ConcurrentQueue<string> Invoked { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public string KindId => "agent-conversation";
        public long RemoteVersion => Interlocked.Read(ref _version);

        public void Say(string line)
        {
            lock (_lines) _lines.Add(line);
            Interlocked.Increment(ref _version);
        }

        public RemoteTileBody DescribeForRemote()
        {
            List<RemoteChatItem> items;
            lock (_lines) items = [.. _lines.Select((l, i) => new RemoteChatItem($"{name}{i}", "assistant", l))];
            return new RemoteTileBody("chat", new RemoteStatus("idle"), new RemoteChat(items, 0, null, null),
                Composer: new RemoteComposer(true, "Message"));
        }

        public Task<RemoteRefusal?> HandleRemoteAsync(RemoteTileCommand command)
        {
            if (command is RemoteInterrupt) return Task.FromResult<RemoteRefusal?>("Nothing to stop.");
            ReceivedQueue.Enqueue(command);
            return Task.FromResult<RemoteRefusal?>(null);
        }

        public IReadOnlyList<TileAction> Actions =>
        [
            new("continue", "Continue", "play"),
            new(TileActionIds.Restart, "Restart", "restart", IsDestructive: true),
        ];

        public Task<TileActionResult> InvokeAsync(string id)
        {
            Invoked.Enqueue(id);
            return Task.FromResult(TileActionResult.Ok);
        }

        public void Dispose() { }
    }
}
