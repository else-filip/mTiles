namespace mTiles.ViewModels;

/// <summary>
/// A key something outside the tile is allowed to press.
/// </summary>
/// <remarks>
/// A closed set, decided in this process at compile time. What reaches it has crossed a network from a
/// device the user paired once and may have left in a coat pocket, and the destination is a shell — so
/// everything not in this enum is nonsense that gets no reply. The first six answer the prompt an agent
/// is waiting on, move through the choices it is offering, and dismiss or back out of what it has put on
/// the screen (Escape). The rest are what a shell and a terminal agent are driven by from a phone with no
/// keyboard of its own for them: completion (Tab), a TUI's mode switch (Shift+Tab — Claude Code's
/// permission cycle), taking back a character, and the interrupt (Ctrl+C) a running command is stopped
/// with.
/// </remarks>
public enum TileKey
{
    Enter,
    Up,
    Down,
    Left,
    Right,
    Escape,
    Tab,
    ShiftTab,
    Backspace,
    CtrlC,
}

/// <summary>
/// Tile content that text and keystrokes can be delivered into — a dictated sentence, and the Enter
/// that sends it.
/// </summary>
/// <remarks>
/// <para>The tile answers for its own input surface. Before this, dictation reached into the terminal
/// tile's cached control and asked whether the shell was still running, which meant every caller of that
/// route had to know what a terminal is.</para>
/// <para>Both methods return false rather than throwing when there is nowhere to put it — a shell that
/// has exited is a destination that will silently swallow text, and saying so is the difference between
/// the phone showing a reason and the user pressing again.</para>
/// </remarks>
public interface ITextInputTile : ITile
{
    /// <summary>Types the text, optionally submitting it.</summary>
    /// <returns>False when there was nowhere to type it.</returns>
    bool TrySendText(string text, bool submit);

    /// <summary>Presses the key.</summary>
    /// <returns>False when there was nothing to press it at.</returns>
    bool TryPressKey(TileKey key);
}
