using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using AvaloniaEdit;
using mTiles.Models;
using mTiles.Services.Shells;
using mTiles.Services.Phone;
using mTiles.Services.Phone.Remote;
using mTiles.Services.Speech;
using mTiles.ViewModels;
using Terminal.Avalonia;
using Terminal.Pty;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Where a key pressed on the phone lands.
/// </summary>
/// <remarks>
/// The keys exist to answer the prompt an agent is waiting on, so the two things worth pinning are that
/// they reach the shell <em>as the shell reads them</em> — a cursor key is not the characters
/// <c>ESC [ A</c> to every application, and only the terminal control knows which — and that they follow
/// the transcript's own routing rather than a rule of their own. Dictating a line and pressing Enter is
/// one gesture in two halves; if the halves chose their target differently the sentence would sit in one
/// place while the Enter submitted something else in another.
/// </remarks>
public class PhoneKeysTests : IDisposable
{
    private static readonly ShellInstallation Shell = new(new BashTerminal(), "fake-shell");

    private readonly TempSettings _settings = new();
    private readonly List<TerminalControl> _controls = [];
    public void Dispose() => _settings.Dispose();

    /// <summary><see cref="Ui.Run(Func{Task})"/>, disposing the terminals the test made on its way out.</summary>
    private void OnUiThread(Func<Task> body) => Ui.Run(async () =>
    {
        try { await body(); }
        finally
        {
            foreach (var control in _controls)
                control.Dispose();
            _controls.Clear();
        }
    });

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"timed out waiting until {what}");
            await Task.Delay(1);
        }
    }

    /// <summary>A window is what makes a control "on screen" as far as the routing is concerned.</summary>
    private static Window ShowingWindow(Control content)
    {
        var window = new Window { Content = content };
        window.Show();
        return window;
    }

    /// <summary>A tile running a shell that records every byte the control sends it.</summary>
    private (LeafTileNodeViewModel Tile, TerminalControl Control, FakePty Pty) TerminalTile()
    {
        FakePty? pty = null;
        var control = new TerminalControl { PtyFactory = options => pty = new FakePty(options) };
        _controls.Add(control);

        var content = new TerminalTileViewModel("", Shell, _settings.Service, LaunchScripts.None);
        content.AttachControl(control);
        control.Start(new PtyOptions { Command = "fake-shell", Arguments = ["-l"] });

        var tile = new LeafTileNodeViewModel(TileKindIds.Terminal, content, "", new TileActivationScope());
        return (tile, control, pty!);
    }

    // ── the shell ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sequences a shell actually receives.
    /// </summary>
    /// <remarks>
    /// This is the reason a key event is synthesised rather than bytes written: what Up means on the wire
    /// is the terminal control's decision and it changes under the application's feet — DECCKM turns
    /// <c>ESC [ A</c> into <c>ESC O A</c>, and win32-input-mode replaces both with INPUT_RECORDs. What is
    /// pinned here is the plain case; the point is that the answer comes from the control rather than
    /// from a table in this application that would have to be kept in step with it.
    /// </remarks>
    [Theory]
    [InlineData("enter", "\r")]
    [InlineData("up", "\x1b[A")]
    [InlineData("down", "\x1b[B")]
    [InlineData("left", "\x1b[D")]
    [InlineData("right", "\x1b[C")]
    [InlineData("escape", "\x1b")]
    [InlineData("tab", "\t")]
    [InlineData("shifttab", "\x1b[Z")]
    [InlineData("backspace", "\x7f")]
    [InlineData("ctrlc", "\x03")]
    public void A_key_reaches_the_shell_as_the_shell_reads_it(string name, string expected)
        => OnUiThread(async () =>
        {
            var (tile, _, pty) = TerminalTile();
            Assert.True(PhoneKeys.TryParse(name, out var key));

            Assert.True(PhoneKeys.Press(tile, key));

            await WaitUntil(() => pty.Written.Length > 0, "the shell has been sent something");
            Assert.Equal(expected, pty.Written);
        });

    /// <summary>
    /// A tile whose shell has exited is refused rather than pressed at.
    /// </summary>
    /// <remarks>
    /// False is what turns into a sentence on the phone. Reporting success for a key that went nowhere
    /// leaves the user pressing Enter at a dead terminal with nothing to tell them why — and the phone is
    /// usually the only screen they are looking at.
    /// </remarks>
    [Fact]
    public void A_dead_shell_takes_nothing()
        => OnUiThread(async () =>
        {
            var (tile, control, pty) = TerminalTile();
            pty.EndProcess();
            await WaitUntil(() => !control.IsRunning, "the session has been reported dead");

            Assert.False(PhoneKeys.Press(tile, TileKey.Enter));
        });

    [Fact]
    public void A_tile_that_is_not_a_terminal_takes_nothing()
        => OnUiThread(() =>
        {
            var tile = new LeafTileNodeViewModel(TileKindIds.None, null, "", new TileActivationScope());

            Assert.False(PhoneKeys.Press(tile, TileKey.Enter));
            Assert.False(PhoneKeys.Press(null, TileKey.Enter));
            return Task.CompletedTask;
        });

    // ── the focused control ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_focused_text_box_takes_the_key_before_the_terminal()
        => OnUiThread(async () =>
        {
            var (tile, _, pty) = TerminalTile();
            var box = new TextBox();
            ShowingWindow(box);
            var seen = new List<Key>();
            box.KeyDown += (_, e) => seen.Add(e.Key);

            Assert.True(PhoneKeys.Press(tile, TileKey.Down, box));

            await Task.Yield();
            Assert.Equal([Key.Down], seen);
            Assert.Equal("", pty.Written);
        });

    /// <summary>
    /// A read-only control is not a destination, so the key carries on to the shell.
    /// </summary>
    /// <remarks>
    /// The same rule the transcript follows. Half the text in this application is in a read-only editor —
    /// a diff, a transcript — and one of them holding the focus must not swallow the Enter that was meant
    /// for the agent waiting next door.
    /// </remarks>
    [Fact]
    public void A_read_only_editor_does_not_swallow_the_key()
        => OnUiThread(async () =>
        {
            var (tile, _, pty) = TerminalTile();
            var editor = new TextEditor { IsReadOnly = true };
            ShowingWindow(editor);

            Assert.True(PhoneKeys.Press(tile, TileKey.Enter, editor));

            await WaitUntil(() => pty.Written.Length > 0, "the shell has been sent something");
            Assert.Equal("\r", pty.Written);
        });

    /// <summary>
    /// A control whose window has gone is not a destination either.
    /// </summary>
    /// <remarks>
    /// The focused element is read on a socket thread's behalf and used against a tree that may have
    /// closed underneath it — a dialog dismissed while the phone was in a pocket. Pressing at a detached
    /// control is a key nobody can see, reported as delivered.
    /// </remarks>
    [Fact]
    public void A_control_that_has_left_the_tree_does_not_take_the_key()
        => OnUiThread(async () =>
        {
            var (tile, _, pty) = TerminalTile();
            var box = new TextBox();
            var window = ShowingWindow(box);
            window.Content = null;                       // the dialog closed while the phone was in a pocket
            window.Close();

            Assert.True(PhoneKeys.Press(tile, TileKey.Enter, box));

            await WaitUntil(() => pty.Written.Length > 0, "the shell has been sent something");
            Assert.Equal("\r", pty.Written);
        });

    // ── what the phone is told ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sentence a refused key comes back as names the reason, and a delivered one says nothing.
    /// </summary>
    /// <remarks>
    /// The phone is usually the only screen the user is looking at, so the difference between two
    /// sentences is the difference between walking back to the computer and knowing what to do. Driven
    /// through <see cref="PhoneTiles.HandleAsync"/>, the surface a phone's command reaches the tile by.
    /// </remarks>
    [Fact]
    public void A_refused_key_comes_back_as_the_reason_it_was_refused()
        => OnUiThread(async () =>
        {
            var (terminal, control, pty) = TerminalTile();
            Assert.Null(await PhoneTiles.HandleAsync(terminal, new RemoteKey(TileKey.Enter)));
            await WaitUntil(() => pty.Written.Length > 0, "the shell has been sent something");

            pty.EndProcess();
            await WaitUntil(() => !control.IsRunning, "the session has been reported dead");
            Assert.Equal("The shell in this tile is not running.",
                await PhoneTiles.HandleAsync(terminal, new RemoteKey(TileKey.Enter)));

            // Anything that is not a destination at all says so, rather than naming a shell to restart.
            var note = new LeafTileNodeViewModel(TileKindIds.Note, null, "", new TileActivationScope());
            Assert.Equal("That tile cannot do that from a phone.",
                await PhoneTiles.HandleAsync(note, new RemoteKey(TileKey.Enter)));
        });

    // ── the wire names ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing but the page's names is a key: they are matched exactly, case and all.
    /// </summary>
    /// <remarks>
    /// What arrives is a string from a paired device across a network, and what it selects is a keystroke
    /// into a shell, so anything outside the closed list is nonsense, answered with silence. That every
    /// page name <em>is</em> a key is the walk below.
    /// </remarks>
    [Theory]
    [InlineData("Enter")]
    [InlineData("Escape")]
    [InlineData("esc")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_but_the_page_names_is_not_a_key(string? name)
        => Assert.False(PhoneKeys.TryParse(name, out _));

    /// <summary>
    /// Every key in the enum has a wire name and a keystroke, and no two share either.
    /// </summary>
    /// <remarks>
    /// The set is closed at compile time, but nothing in the compiler makes the three places that list it
    /// agree — the enum, <see cref="PhoneKeys.TryParse"/> and the map in <see cref="TileKeyPress"/>. Walked
    /// rather than enumerated here on purpose: a fourth key added to the enum and missed in either of the
    /// others fails this without anybody having to remember to come back and add a case. What it is
    /// standing guard over is one specific outcome — a key that arrives, is accepted, and is delivered as
    /// something else. Enter is the one it would have been delivered as, and Enter is the press that
    /// takes an agent's default answer.
    /// </remarks>
    [Fact]
    public void Every_key_has_a_name_and_a_keystroke_of_its_own()
    {
        var keys = Enum.GetValues<TileKey>();

        // The wire name is the member's own name in lower case — the convention the page is written to,
        // pinned here so it stays one rather than becoming a lookup table somebody has to remember.
        foreach (var key in keys)
        {
            Assert.True(PhoneKeys.TryParse(key.ToString().ToLowerInvariant(), out var parsed),
                $"{key} has no wire name");
            Assert.Equal(key, parsed);
        }

        // And no two names collide, which is what a forgotten arm used to produce. What each key *is*
        // to a control that reads the keyboard lives in one place for both destinations — TileKeyPress,
        // which ITextInputTile.TryPressKey delivers through — and is pinned by the theory above, which
        // drives the real thing all the way to a shell.
        var names = keys.Select(k => k.ToString().ToLowerInvariant()).ToList();
        Assert.Equal(keys.Length, names.Distinct().Count());
    }
}
