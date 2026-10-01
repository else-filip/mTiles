using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using Terminal.Avalonia;

namespace mTiles.Views;

/// <summary>
/// A terminal and nothing else, drawn over the window, running one CLI's own login — with a header saying
/// what is about to be asked and why.
/// </summary>
/// <remarks>
/// <para>An overlay rather than a tile beside the active one, which is how Settings' Sign in reaches a
/// terminal: a login is a question with an answer, and a tile is something that stays in the workspace's
/// layout — and in its saved file — after the question is gone.</para>
/// <para>Answers <c>true</c> when the login command exits cleanly, which is what closes it by itself;
/// <c>null</c> when the user closed it. A command that fails leaves the window open on its output, since
/// that output is the only account of why.</para>
/// <para>Escape goes to the CLI while it runs — the terminal takes the key before the overlay's own
/// handler sees it — so only the X closes the window then. Once the command has ended, Escape closes it
/// too.</para>
/// </remarks>
public sealed class AgentSignInView : UserControl, OverlayHost.IOverlayHeader, OverlayHost.IFocusOnOpen
{
    private readonly AgentLoginLaunch _launch;
    private readonly TerminalControl _terminal;
    private readonly TextBlock _status;
    private bool _started;

    public AgentSignInView(AgentLoginLaunch launch, AppSettings settings)
    {
        _launch = launch;

        _terminal = new TerminalControl
        {
            FontFamily = new FontFamily(settings.TerminalFontFamily),
            FontSize = TextScale.TerminalFontSize(settings),
            ScrollbackCapacity = 2000,
            Palette = ThemeBridge.ToPalette(Models.TerminalTheme.GetByName(settings.ColorThemeName)),
        };

        var title = Text(launch.Title, "TextStrong", "FontBase");
        title.FontWeight = FontWeight.SemiBold;

        var instructions = Text(launch.Instructions, "TextSecondary", "FontSm");
        instructions.TextWrapping = TextWrapping.Wrap;
        instructions.Margin = new Thickness(0, 4, 0, 0);

        _status = Text("", "WarnText", "FontSm");
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 4, 0, 0);
        _status.IsVisible = false;

        OverlayHeader = new StackPanel
        {
            Margin = new Thickness(0, 0, 8, 10),
            Children = { title, instructions, _status },
        };

        // The terminal's own ground to the edge of the card, inset the way a terminal tile's content is.
        var ground = new Border { Padding = new Thickness(8), Child = _terminal };
        ground.Bind(Border.BackgroundProperty, ground.GetResourceObservable("BgBase").ToBinding());
        Content = ground;
    }

    public Control? OverlayHeader { get; }

    /// <summary>Opens the login over <paramref name="owner"/>'s window.</summary>
    /// <returns>True when the login command finished cleanly, null when the window was closed, and null
    /// too when there is no window to draw it in.</returns>
    public static Task<bool?> ShowAsync(Visual owner, AgentLoginLaunch launch, AppSettings settings)
    {
        if (OverlayHost.For(owner) is not { } host) return Task.FromResult<bool?>(null);
        return host.ShowAsync<bool?>(new AgentSignInView(launch, settings),
            OverlaySize.Fraction(0.6, 0.7, 560, 420));
    }

    public void FocusOnOpen() => _terminal.Focus();

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_started) return;
        _started = true;

        try
        {
            // After layout, so the CLI is started at the size it is drawn at rather than at 80x24 and
            // reflowed a moment later — the same wait a terminal tile makes before its first launch.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (_terminal.IsDisposed) return;

            var session = await ShellStarter.StartAsync(_terminal, _launch.WorkingDirectory, _launch.Executable,
                _launch.Arguments, environment: _launch.Environment);
            // Awaited by its id rather than heard through Exited: a login that fails at once can end before
            // the id is back, and a report already delivered is one no filter set afterwards will see.
            Report(await _terminal.WhenSessionEndedAsync(session));
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            Trace.TraceWarning($"[Login] Could not start {_launch.Executable}: {ex}");
            Say($"The login could not be started: {ex.Message}");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Closing the window is the user leaving the login; a CLI still waiting on a browser callback has
        // nobody left to answer it.
        _terminal.Dispose();
    }

    private void Report(SessionExitedEventArgs e)
    {
        if (e.ExitCode == 0)
        {
            OverlayHost.CloseWith(this, true);
            return;
        }

        Say(e.ExitCode is { } code
            ? $"The login ended with exit code {code}. What it printed is above; close this window to go back."
            : "The login ended without an exit code. Close this window to go back.");
    }

    private void Say(string sentence)
    {
        _status.Text = sentence;
        _status.IsVisible = true;
    }

    private static TextBlock Text(string text, string brush, string size)
    {
        var block = new TextBlock { Text = text };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(brush).ToBinding());
        block.Bind(TextBlock.FontSizeProperty, block.GetResourceObservable(size).ToBinding());
        return block;
    }
}
