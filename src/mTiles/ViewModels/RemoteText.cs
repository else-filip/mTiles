namespace mTiles.ViewModels;

/// <summary>
/// Text a phone sends into a composer must not replace a draft somebody is typing on the computer:
/// that draft exists nowhere else, and it would vanish without a word.
/// </summary>
public static class RemoteText
{
    public const string DraftInTheWay =
        "There is an unsent message in this tile on the computer. Send or clear it there first.";

    /// <param name="seen">The draft as the phone was last shown it. A draft still exactly that has been
    /// read on the phone — and usually edited there — so replacing it loses nothing.</param>
    public static bool WouldOverwrite(string? draft, string incoming, string? seen = null) =>
        !string.IsNullOrWhiteSpace(draft) && draft != incoming && draft != seen;
}
