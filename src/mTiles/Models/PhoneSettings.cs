namespace mTiles.Models;

/// <summary>Reaching this application from a phone: dictating into a tile, and watching and answering
/// the agents in it.</summary>
public sealed class PhoneSettings
{
    /// <summary>The page a phone is sent to, the invitation code riding in its fragment.</summary>
    /// <remarks>Hosted on GitHub Pages, built from <c>site/phone/</c> in this repository — see
    /// <c>docs/adr/0006-phone-over-relays.md</c>. Not on any settings page: it exists so a developer can
    /// point a build at a copy served from their own machine.</remarks>
    public const string DefaultPageUrl = "https://b-y-t-e.github.io/mTiles/phone/";

    /// <summary>
    /// Whether the bridge stays connected to the relay once the QR panel is closed, even with no phone
    /// paired yet.
    /// </summary>
    /// <remarks>
    /// With a phone paired the bridge runs anyway — a paired phone that cannot reach the machine is the
    /// feature not working — so what this adds is only the case of nobody paired. Off by default: a machine
    /// that has never shown a QR code has no reason to hold a connection to Tailscale's relays open.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether any phone was paired when the bridge last looked, which is what decides whether it
    /// starts with the application.
    /// </summary>
    /// <remarks>Kept here rather than read out of the link's own file, because that file is the
    /// library's compatibility surface and reading it means parsing a format that is not ours to rely
    /// on. Written by the bridge only, never by the settings page.</remarks>
    public bool HasPairedDevices { get; set; }

    /// <summary>Where the phone page is. See <see cref="DefaultPageUrl"/>.</summary>
    public string PageUrl
    {
        get => _pageUrl;
        set => _pageUrl = string.IsNullOrWhiteSpace(value) ? DefaultPageUrl : value.Trim();
    }
    private string _pageUrl = DefaultPageUrl;

    /// <summary>Whether transcription from a phone presses Enter, independently of the local setting.</summary>
    /// <remarks>
    /// Its own switch because the gesture is not the same one. At the keyboard the user is looking at the
    /// terminal and can see what landed there before pressing Enter themselves; holding a phone they are
    /// often not looking at the screen at all — which is exactly why pressing Enter for them is worth
    /// offering, and exactly why it is not the default. Running a command in a terminal from a device the
    /// user is not watching is not something to opt anybody into; the switch exists so they can choose it
    /// knowingly, for the gesture where it helps.
    /// </remarks>
    public bool AutoSubmitEnter { get; set; }
}
