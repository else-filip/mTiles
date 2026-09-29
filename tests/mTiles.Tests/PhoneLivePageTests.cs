using System.ComponentModel;
using mTiles.Models;
using mTiles.Services.Phone;
using mTiles.Services.Phone.Remote;
using mTiles.Services.Speech;
using mTiles.ViewModels;
using Tailcat.Link.Storage;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A fact that talks to Tailscale's public relays, and so is skipped unless <c>MTILES_LIVE_PHONE</c>
/// names a directory to exchange files with the browser driving the page.
/// </summary>
public sealed class LivePhoneFactAttribute : FactAttribute
{
    public LivePhoneFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MTILES_LIVE_PHONE")))
            Skip = "Live phone: set MTILES_LIVE_PHONE to a directory, and drive the page from a browser.";
    }
}

/// <summary>
/// The bridge over the real relays, for a real browser to pair with: the check that the page and this
/// host speak the same protocol, which no test on one side alone can make.
/// </summary>
/// <remarks>
/// Writes the pairing URL to <c>$MTILES_LIVE_PHONE/invite.txt</c>, logs every command the page sends to
/// <c>commands.txt</c>, and serves until a file called <c>stop</c> appears there or five minutes pass.
/// The page is served by <c>node site/phone/serve.mjs</c>; <c>MTILES_LIVE_PHONE_PAGE</c> overrides where
/// it is. Nothing is stored: the link's identity is in memory.
/// </remarks>
public sealed class PhoneLivePageTests
{
    [LivePhoneFact]
    public async Task A_browser_can_pair_and_drive_the_page()
    {
        var directory = Environment.GetEnvironmentVariable("MTILES_LIVE_PHONE")!;
        Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, "commands.txt");
        File.WriteAllText(log, "");

        using var settings = new TempSettings();
        settings.Service.Settings.Phone.PageUrl =
            Environment.GetEnvironmentVariable("MTILES_LIVE_PHONE_PAGE") ?? "http://localhost:8787/";

        var workspaces = new DemoWorkspaces(line => File.AppendAllText(log, line + Environment.NewLine));
        var router = new RoutedAudioCapture(new IdleMicrophone(), new PhoneAudioCapture());
        await using var manager = new PhoneBridgeManager(
            settings.Service,
            new DictationService(settings.Service, router),
            router,
            workspaces,
            new InlineUiDispatcher(),
            () => new Tailcat.Link.LinkOptions
            {
                Store = new InMemoryLinkStore(),
                MaxPeers = PhoneBridgeManager.MaxDevices,
                LoggerFactory = new TraceLoggerFactory(),
            });

        Assert.True(await manager.StartAsync(), manager.LastError);
        var invitation = await manager.InviteAsync();
        File.WriteAllText(Path.Combine(directory, "invite.txt"), invitation.Url);

        var stop = Path.Combine(directory, "stop");
        File.Delete(stop);
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var tick = 0;
        while (!File.Exists(stop) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            if (++tick % 8 == 0) workspaces.Chat.Stream();
            await manager.PushNowAsync();
        }

        File.WriteAllText(Path.Combine(directory, "devices.txt"),
            string.Join(Environment.NewLine, manager.Devices.Select(d => $"{d.Name} connected={d.IsConnected}")));
    }

    /// <summary>A workspace with a conversation, a terminal and a note in it.</summary>
    private sealed class DemoWorkspaces : IPhoneWorkspaces
    {
        public readonly DemoChat Chat;
        private readonly LeafTileNodeViewModel _chat, _terminal, _note, _goal;
        private readonly TileNodeViewModel _root;

        public DemoWorkspaces(Action<string> log)
        {
            Chat = new DemoChat(log);
            var scope = new TileActivationScope();
            _chat = new LeafTileNodeViewModel("agent-conversation", Chat, "", scope) { TileId = "chat", TileName = "Agent#1" };
            _terminal = new LeafTileNodeViewModel("agent", new DemoTerminal(log), "", scope) { TileId = "term", TileName = "brave-otter" };
            _note = new LeafTileNodeViewModel("note", null, "", scope) { TileId = "note", TileName = "Note#1" };
            _goal = new LeafTileNodeViewModel("goal", new DemoTerminal(log), "", scope) { TileId = "goal", TileName = "Goal#1" };
            _root = new SplitTileNodeViewModel(Avalonia.Layout.Orientation.Vertical, _chat,
                new SplitTileNodeViewModel(Avalonia.Layout.Orientation.Horizontal, _terminal,
                    new SplitTileNodeViewModel(Avalonia.Layout.Orientation.Vertical, _note, _goal)) { SplitRatio = 0.6 })
            { SplitRatio = 0.55 };
            _chat.IsActive = true;
        }

        public IReadOnlyList<RemoteWorkspace> List() =>
        [
            new("ws", "mterminal", "master", "blocked", Loaded: true, Current: true, Favorite: true),
            new("ws2", "Terminal.Avalonia", "feature/read-screen", "working", Loaded: true, Current: false, Favorite: false),
            new("ws3", "Home directory", null, "unknown", Loaded: false, Current: false, Favorite: false),
        ];

        public RemoteLayout? Layout(string workspaceId) =>
            new(workspaceId, "mterminal", LayoutProjection.Project(_root, PhoneTiles.IsReachable));

        public bool Open(string workspaceId) => true;

        public (LeafTileNodeViewModel Tile, string WorkspaceId)? Find(string tileId) => tileId switch
        {
            "chat" => (_chat, "ws"),
            "term" => (_terminal, "ws"),
            "goal" => (_goal, "ws"),
            _ => null,
        };

        public LeafTileNodeViewModel? ActiveTile => _chat;
    }

    private sealed class DemoChat(Action<string> log) : IRemoteViewTile, ITileActions
    {
        private long _version;
        private int _streamed;
        private bool _approved;

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public string KindId => "agent-conversation";
        public long RemoteVersion => Interlocked.Read(ref _version);

        public void Stream()
        {
            if (_streamed++ < 12) Interlocked.Increment(ref _version);
        }

        public RemoteTileBody DescribeForRemote()
        {
            var items = new List<RemoteChatItem>
            {
                new("1", "user", "Add a **read-only** screen reader to the terminal control, and test it.", Markdown: true),
                new("2", "work", "4 steps", Work:
                [
                    new("Read src/Terminal.Emulation/Terminal.cs", "done"),
                    new("Edit Terminal.cs", "done"),
                    new("dotnet test --filter ReadTextTests", "failed"),
                    new("Edit ReadTextTests.cs", "done"),
                ]),
                new("3", "assistant",
                    "Added `ReadText(int scrollbackLines)`:\n\n- one string per row, trailing blanks trimmed\n- the blank tail dropped\n\n```diff\n+    public IReadOnlyList<string> ReadText(int scrollbackLines = 0)\n-    // nothing here\n```\n\n" +
                    string.Concat(Enumerable.Repeat("Streaming more words. ", _streamed)),
                    Markdown: true, Streaming: _streamed < 12),
            };

            RemotePending? pending = _approved ? null : new RemotePending("approval", "req1", "Run dotnet test?",
                "dotnet test tests/Terminal.Emulation.Tests",
                [new("Accept", "Yes", "primary"), new("AcceptForSession", "Yes, for this session", "neutral"), new("Cancel", "No, stop", "danger")]);

            return new RemoteTileBody("chat",
                new RemoteStatus(_approved ? "working" : "blocked", _approved ? "Running tests" : "Waiting for your answer",
                    "Claude Code · opus-5", 42),
                new RemoteChat(items, 3, pending,
                    [new("Read the engine", "done"), new("Add ReadText", "running"), new("Release 0.4.2", "pending")]),
                Composer: new RemoteComposer(true, "Message the agent", CanInterrupt: _approved));
        }

        public Task<RemoteRefusal?> HandleRemoteAsync(RemoteTileCommand command)
        {
            log($"chat {command}");
            if (command is RemoteChoose) { _approved = true; Interlocked.Increment(ref _version); }
            return Task.FromResult<RemoteRefusal?>(null);
        }

        public IReadOnlyList<TileAction> Actions => [new("continue", "Continue", "play")];

        public Task<TileActionResult> InvokeAsync(string id)
        {
            log($"chat action {id}");
            return Task.FromResult(TileActionResult.Ok);
        }

        public void Dispose() { }
    }

    private sealed class DemoTerminal(Action<string> log) : IRemoteViewTile
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public string KindId => "agent";
        public long RemoteVersion => 1;

        public RemoteTileBody DescribeForRemote() => new("terminal",
            new RemoteStatus("working", "✳ Compiling…", "codex · gpt-5.5"),
            Screen: new RemoteScreen(
            [
                "PS D:\\work\\sources\\mterminal> dotnet build",
                "  mTiles.AgentSessions -> bin\\Debug\\net10.0\\mTiles.AgentSessions.dll",
                "  mTiles -> bin\\Debug\\net10.0\\mTiles.dll",
                "",
                "Kompilacja powiodła się.",
                "    Ostrzeżenia: 0",
                "    Liczba błędów: 0",
                "PS D:\\work\\sources\\mterminal> _",
            ], "pwsh"),
            Composer: new RemoteComposer(true, "Type into the terminal", Keys: true));

        public Task<RemoteRefusal?> HandleRemoteAsync(RemoteTileCommand command)
        {
            log($"terminal {command}");
            return Task.FromResult<RemoteRefusal?>(null);
        }

        public void Dispose() { }
    }
}
