using System.Text.Json;
using System.Text.Json.Serialization;
using mTiles.Services.Phone.Remote;

namespace mTiles.Services.Phone;

/// <summary>
/// The words the phone page and this application say to each other over the link.
/// </summary>
/// <remarks>
/// <para><b>Requests</b> go from the page to here as JSON text with a <c>type</c>; every one is answered
/// with <c>{"ok":true,…}</c> or <c>{"ok":false,"error":"…"}</c>, and nonsense — malformed JSON, a number
/// where a string belongs, a type this build does not know — gets the second, never an exception out
/// of the handler. <b>Pushes</b> go the other way as notifications, also with a <c>type</c>.
/// <b>Audio</b> is a channel of its own (<see cref="AudioChannel"/>), because a request per frame would
/// be a round trip and a ledger entry each, and because what is on it must stay in order.</para>
/// <para>The page is hosted separately from this build and either can be the newer one, so
/// <see cref="Version"/> is exchanged first and a page that finds a number it does not speak says so
/// instead of guessing.</para>
/// </remarks>
internal static class PhoneProtocol
{
    /// <summary>What this build speaks. Bumped when a page relies on something an older build would
    /// refuse — 2 added four keys, the <c>new</c> request, the send's mode/replaces/discard and the audio
    /// header's <c>toDraft</c>.</summary>
    public const int Version = 2;

    /// <summary>The oldest page this build still answers: everything since has only been added, so an
    /// older page is not locked out of a newer build.</summary>
    public const int OldestPageVersion = 1;

    /// <summary>Whether a page speaking <paramref name="pageVersion"/> can be served by this build.</summary>
    public static bool Accepts(int pageVersion) => pageVersion is >= OldestPageVersion and <= Version;

    /// <summary>The name the phone app's link is stored under, on both ends.</summary>
    public const string AppName = "mtiles-phone";

    /// <summary>
    /// The channel a phone streams a recording on.
    /// </summary>
    /// <remarks>
    /// One channel per utterance, ordered within itself. The first frame is a JSON header
    /// (<c>{"tileId":…,"sampleRate":…}</c>); every frame after it is 16-bit little-endian mono PCM at that
    /// rate; a frame of exactly one byte, <see cref="CancelMarker"/>, throws the recording away; and the
    /// channel closing on purpose is "that was the end of the sentence". A channel that ends with the
    /// session instead is a cancel too — half a sentence typed into a terminal is worse than none.
    /// </remarks>
    public const string AudioChannel = "audio";

    /// <summary>See <see cref="AudioChannel"/>. A PCM frame is never one byte long.</summary>
    public const byte CancelMarker = 0;

    /// <summary>The largest request a phone may send. Everything it has to say is a few hundred bytes;
    /// the largest is a typed message.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Ok(object? body = null) =>
        body is null
            ? """{"ok":true}"""u8.ToArray()
            : JsonSerializer.SerializeToUtf8Bytes(body, Json);

    public static byte[] Error(string message, string? code = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = message, code }, Json);

    /// <summary>The code a page acts on for a refusal it must recognise — never the sentence, which is for
    /// reading and may be reworded.</summary>
    public const string DraftInTheWayCode = ViewModels.RemoteText.DraftInTheWayCode;

    /// <summary>A tile's refusal as the phone receives it: the sentence, and a code where the page acts on it.</summary>
    public static byte[] Refusal(ViewModels.RemoteRefusal refusal) => Error(refusal.Message, refusal.Code);

    public static byte[] Push(string type, object body)
    {
        // Merged rather than wrapped, so the page reads `message.tile` and not `message.body.tile`.
        var element = JsonSerializer.SerializeToElement(body, Json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            foreach (var property in element.EnumerateObject())
                property.WriteTo(writer);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads a request. Null for anything that is not one — the caller answers that with an error and
    /// nothing else.
    /// </summary>
    public static PhoneRequest? Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is 0 or > MaxRequestBytes)
            return null;

        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            // Caught by type below rather than guarded per read: `{"tileId":123}` parses, and GetString
            // then throws InvalidOperationException, which is how JsonElement says "that is there, but it
            // is not the kind you asked for". The next property added here would need remembering
            // otherwise.
            string? S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
                ? v.GetString()
                : null;
            bool B(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

            return S("type") switch
            {
                "hello" => new HelloRequest(root.TryGetProperty("protocol", out var p) && p.TryGetInt32(out var n) ? n : 0),
                "workspaces" => new WorkspacesRequest(),
                "unpair" => new UnpairRequest(),
                "layout" when S("workspaceId") is { Length: > 0 } ws => new LayoutRequest(ws),
                "open" when S("workspaceId") is { Length: > 0 } ws => new OpenWorkspaceRequest(ws),
                "watch" => new WatchRequest(S("workspaceId"), S("tileId")),
                "tile" when S("tileId") is { Length: > 0 } t => new TileRequest(t),
                "send" when S("tileId") is { Length: > 0 } t && S("text") is { } text =>
                    new TileCommandRequest(t, new RemoteSendText(text, B("submit"), S("mode"), S("replaces"),
                        B("discard"))),
                "new" when S("tileId") is { Length: > 0 } t => new TileCommandRequest(t, new RemoteNewConversation()),
                "draft" when S("tileId") is { Length: > 0 } t && S("text") is { } text =>
                    new TileCommandRequest(t, new RemoteDraft(text, S("seen"))),
                "pick" when S("tileId") is { Length: > 0 } t && S("picker") is { Length: > 0 } picker
                            && S("value") is { Length: > 0 } value =>
                    new TileCommandRequest(t, new RemotePick(picker, value)),
                "compact" when S("tileId") is { Length: > 0 } t => new TileCommandRequest(t, new RemoteCompact()),
                "openConversation" when S("tileId") is { Length: > 0 } t && S("conversationId") is { Length: > 0 } c =>
                    new TileCommandRequest(t, new RemoteOpenConversation(c)),
                "conversations" when S("tileId") is { Length: > 0 } t => new ConversationsRequest(t),
                "key" when S("tileId") is { Length: > 0 } t && PhoneKeys.TryParse(S("key"), out var key) =>
                    new TileCommandRequest(t, new RemoteKey(key)),
                "choose" when S("tileId") is { Length: > 0 } t && S("pendingId") is { Length: > 0 } pending
                              && S("optionId") is { Length: > 0 } option =>
                    new TileCommandRequest(t, new RemoteChoose(pending, option)),
                "answer" when S("tileId") is { Length: > 0 } t && S("pendingId") is { Length: > 0 } pending
                              && Answers(root) is { } answers =>
                    new TileCommandRequest(t, new RemoteAnswer(pending, answers)),
                "interrupt" when S("tileId") is { Length: > 0 } t => new TileCommandRequest(t, new RemoteInterrupt()),
                "action" when S("tileId") is { Length: > 0 } t && S("id") is { Length: > 0 } id => new ActionRequest(t, id),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a recording's header frame. Null when it is not one this build accepts.
    /// </summary>
    /// <remarks>Rates outside 8–192 kHz are not microphones. Refused rather than trusted, because the
    /// value sizes a resampling kernel and comes from the network.</remarks>
    public static AudioHeader? ParseAudioHeader(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is 0 or > 4096)
            return null;

        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("sampleRate", out var r) || !r.TryGetInt32(out var rate)
                || rate is < 8_000 or > 192_000)
                return null;

            var tileId = root.TryGetProperty("tileId", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            var toDraft = root.TryGetProperty("toDraft", out var d) && d.ValueKind == JsonValueKind.True;
            return new AudioHeader(string.IsNullOrEmpty(tileId) ? null : tileId, rate, toDraft);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? Answers(JsonElement root)
    {
        if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var property in answers.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => [property.Value.GetString()!],
                JsonValueKind.Array => [.. property.Value.EnumerateArray().Select(v => v.GetString()!)],
                _ => throw new FormatException("an answer is text or a list of text"),
            };
        }
        return result;
    }
}

internal abstract record PhoneRequest;

internal sealed record HelloRequest(int Protocol) : PhoneRequest;

internal sealed record WorkspacesRequest : PhoneRequest;

/// <summary>The phone logging itself out: this machine forgets it, exactly as Unpair in the panel does.
/// Only ever about the phone that asks — a phone cannot unpair another.</summary>
internal sealed record UnpairRequest : PhoneRequest;

internal sealed record LayoutRequest(string WorkspaceId) : PhoneRequest;

internal sealed record OpenWorkspaceRequest(string WorkspaceId) : PhoneRequest;

/// <summary>What the phone is looking at, so what it is pushed is what it would otherwise ask for.
/// Either may be null: the workspace list, a layout, or one tile.</summary>
internal sealed record WatchRequest(string? WorkspaceId, string? TileId) : PhoneRequest;

internal sealed record TileRequest(string TileId) : PhoneRequest;

/// <summary>The stored conversations a tile can be pointed at.</summary>
internal sealed record ConversationsRequest(string TileId) : PhoneRequest;

internal sealed record TileCommandRequest(string TileId, RemoteTileCommand Command) : PhoneRequest;

internal sealed record ActionRequest(string TileId, string ActionId) : PhoneRequest;

/// <param name="ToDraft">The transcript goes back to the phone's own text box, to be read and edited
/// there, and into no tile. What a page that can show it asks for.</param>
internal sealed record AudioHeader(string? TileId, int SampleRate, bool ToDraft = false);
