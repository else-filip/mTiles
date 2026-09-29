namespace mTiles.ViewModels;

/// <summary>
/// Why a tile refused what a phone asked: the sentence, for reading, and — where the page acts on the
/// refusal rather than only showing it — a code, which the sentence can be reworded without moving.
/// </summary>
public sealed record RemoteRefusal(string Message, string? Code = null)
{
    /// <summary>Most refusals are only read, so a sentence alone is one — and no sentence is no refusal.</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(message))]
    public static implicit operator RemoteRefusal?(string? message) => message is null ? null : new(message);

    public override string ToString() => Message;
}

/// <summary>
/// Text a phone sends into a composer must not replace a draft somebody is typing on the computer:
/// that draft exists nowhere else, and it would vanish without a word.
/// </summary>
public static class RemoteText
{
    /// <summary>The code the page stops mirroring its box on.</summary>
    public const string DraftInTheWayCode = "draftInTheWay";

    public static readonly RemoteRefusal DraftInTheWay = new(
        "There is an unsent message in this tile on the computer. Send or clear it there first.",
        DraftInTheWayCode);

    /// <param name="seen">The draft as the phone was last shown it. A draft still exactly that has been
    /// read on the phone — and usually edited there — so replacing it loses nothing.</param>
    public static bool WouldOverwrite(string? draft, string incoming, string? seen = null) =>
        !string.IsNullOrWhiteSpace(draft) && draft != incoming && draft != seen;
}
