using System.Text.RegularExpressions;

namespace mTiles.Services.Phone.Remote;

/// <summary>
/// The line on a tile's card in a phone's miniature, and when the tile last did something.
/// </summary>
/// <remarks>Pure apart from the stamps, which are the bridge's own memory of when a line changed for a tile
/// that cannot say so itself — a terminal's last line has no timestamp anywhere.</remarks>
public sealed partial class TilePreviews(Func<DateTimeOffset>? clock = null)
{
    /// <summary>The most of a line a card is sent. A card shows two or three lines of a narrow column.</summary>
    public const int MaxLength = 140;

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, (string? Text, DateTimeOffset? At)> _seen = [];

    /// <summary>What the card says, with the time filled in from the stamps where the tile had none.
    /// UI thread only.</summary>
    /// <remarks>A line first seen gets no time: when it last changed is not known, and "just now" on a tile
    /// that has been idle for an hour is the one wrong answer worse than none.</remarks>
    public RemotePreview? For(string tileId, TilePreview? preview)
    {
        if (preview is null) return null;
        var text = OneLine(preview.Text);

        var at = preview.ChangedAt;
        if (at is null)
        {
            if (_seen.TryGetValue(tileId, out var seen))
                at = seen.Text == text ? seen.At : _clock();
            _seen[tileId] = (text, at);
        }

        return new RemotePreview(text, preview.ContextPercent, at?.ToUnixTimeMilliseconds());
    }

    /// <summary>The first line that says anything, without markdown's own marks and clipped.</summary>
    public static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var raw in text.Split('\n'))
        {
            var line = Markers().Replace(raw.Trim(), "");
            line = line.Replace("**", "").Replace("`", "");
            line = Spaces().Replace(line, " ").Trim();
            if (line.Length == 0 || line.All(c => c is '-' or '=' or '*' or '_' or '─' or '━' or '|')) continue;
            return line.Length <= MaxLength ? line : line[..(MaxLength - 1)].TrimEnd() + "…";
        }
        return null;
    }

    /// <summary>The last line of a screen that has anything on it.</summary>
    public static string? LastLine(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (OneLine(lines[i]) is { } line)
                return line;
        }
        return null;
    }

    [GeneratedRegex(@"^(#{1,6}\s+|>\s*|[-*+]\s+|\d+[.)]\s+)+")]
    private static partial Regex Markers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
