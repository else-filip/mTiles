using System.Text;
using System.Text.Json;
using mTiles.Services.Phone;
using mTiles.Services.Phone.Remote;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a phone may say, read the one way it is read.
/// </summary>
/// <remarks>
/// What arrives here has crossed the relay from a device paired once and perhaps left in a coat pocket,
/// and what it selects is a keystroke into a shell — so the rule argued in this table is that anything
/// that is not exactly a request is nothing, and is answered with an error rather than an exception.
/// </remarks>
public sealed class PhoneProtocolTests
{
    private static PhoneRequest? Parse(string json) => PhoneProtocol.Parse(Encoding.UTF8.GetBytes(json));

    [Theory]
    [InlineData("""{"type":"hello","protocol":1}""", typeof(HelloRequest))]
    [InlineData("""{"type":"workspaces"}""", typeof(WorkspacesRequest))]
    [InlineData("""{"type":"unpair"}""", typeof(UnpairRequest))]
    [InlineData("""{"type":"layout","workspaceId":"w"}""", typeof(LayoutRequest))]
    [InlineData("""{"type":"open","workspaceId":"w"}""", typeof(OpenWorkspaceRequest))]
    [InlineData("""{"type":"watch"}""", typeof(WatchRequest))]
    [InlineData("""{"type":"watch","workspaceId":"w","tileId":"t"}""", typeof(WatchRequest))]
    [InlineData("""{"type":"tile","tileId":"t"}""", typeof(TileRequest))]
    [InlineData("""{"type":"send","tileId":"t","text":"hi","submit":true}""", typeof(TileCommandRequest))]
    [InlineData("""{"type":"key","tileId":"t","key":"enter"}""", typeof(TileCommandRequest))]
    [InlineData("""{"type":"choose","tileId":"t","pendingId":"p","optionId":"Accept"}""", typeof(TileCommandRequest))]
    [InlineData("""{"type":"answer","tileId":"t","pendingId":"p","answers":{"q":["a"],"r":"b"}}""", typeof(TileCommandRequest))]
    [InlineData("""{"type":"interrupt","tileId":"t"}""", typeof(TileCommandRequest))]
    [InlineData("""{"type":"action","tileId":"t","id":"commit"}""", typeof(ActionRequest))]
    public void A_request_is_read_as_what_it_says(string json, Type expected) =>
        Assert.IsType(expected, Parse(json));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"type":"format-the-disk"}""")]
    [InlineData("""{"type":123}""")]
    [InlineData("""{"type":"tile","tileId":123}""")]
    [InlineData("""{"type":"tile"}""")]
    [InlineData("""{"type":"key","tileId":"t","key":"Enter"}""")]
    [InlineData("""{"type":"key","tileId":"t","key":"f13"}""")]
    [InlineData("""{"type":"send","tileId":"t"}""")]
    [InlineData("""{"type":"answer","tileId":"t","pendingId":"p","answers":{"q":[1]}}""")]
    [InlineData("""{"type":"answer","tileId":"t","pendingId":"p","answers":{"q":{}}}""")]
    [InlineData("""{"type":"action","tileId":"t"}""")]
    public void Anything_else_is_nothing(string json) => Assert.Null(Parse(json));

    [Fact]
    public void A_refusal_the_page_acts_on_carries_the_tiles_own_code()
    {
        var reworded = RemoteText.DraftInTheWay with { Message = "Worded some other way." };
        using var answer = JsonDocument.Parse(PhoneProtocol.Refusal(reworded));
        Assert.Equal("Worded some other way.", answer.RootElement.GetProperty("error").GetString());
        Assert.Equal(PhoneProtocol.DraftInTheWayCode, answer.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void A_refusal_that_is_only_read_carries_no_code()
    {
        using var answer = JsonDocument.Parse(PhoneProtocol.Refusal("That key does nothing here."));
        Assert.Equal(JsonValueKind.Null, answer.RootElement.TryGetProperty("code", out var c) ? c.ValueKind : JsonValueKind.Null);
    }

    [Fact]
    public void A_request_past_the_limit_is_not_read()
    {
        var text = new string('x', PhoneProtocol.MaxRequestBytes);
        Assert.Null(Parse($$"""{"type":"send","tileId":"t","text":"{{text}}"}"""));
    }

    [Fact]
    public void Answers_come_back_as_lists_whether_one_or_many()
    {
        var request = Assert.IsType<TileCommandRequest>(
            Parse("""{"type":"answer","tileId":"t","pendingId":"p","answers":{"q":["a","b"],"r":"c"}}"""));
        var answer = Assert.IsType<RemoteAnswer>(request.Command);

        Assert.Equal(["a", "b"], answer.Answers["q"]);
        Assert.Equal(["c"], answer.Answers["r"]);
    }

    [Fact]
    public void A_key_is_one_of_the_closed_set()
    {
        var request = Assert.IsType<TileCommandRequest>(Parse("""{"type":"key","tileId":"t","key":"escape"}"""));
        Assert.Equal(new RemoteKey(TileKey.Escape), request.Command);
    }

    [Theory]
    [InlineData("tab", TileKey.Tab)]
    [InlineData("shifttab", TileKey.ShiftTab)]
    [InlineData("backspace", TileKey.Backspace)]
    [InlineData("ctrlc", TileKey.CtrlC)]
    public void The_keys_a_shell_is_driven_by_are_in_the_set(string name, TileKey key)
    {
        var request = Assert.IsType<TileCommandRequest>(Parse($$"""{"type":"key","tileId":"t","key":"{{name}}"}"""));
        Assert.Equal(new RemoteKey(key), request.Command);
    }

    [Fact]
    public void A_send_carries_its_mode_the_draft_it_replaces_and_the_consent_to_start_over()
    {
        var request = Assert.IsType<TileCommandRequest>(Parse(
            """{"type":"send","tileId":"t","text":"x","submit":true,"mode":"run","replaces":"old","discard":true}"""));
        Assert.Equal(new RemoteSendText("x", true, "run", "old", true), request.Command);
    }

    [Fact]
    public void A_plain_send_from_an_older_page_asks_for_none_of_that()
    {
        var request = Assert.IsType<TileCommandRequest>(Parse("""{"type":"send","tileId":"t","text":"x"}"""));
        Assert.Equal(new RemoteSendText("x", false), request.Command);
    }

    [Fact]
    public void Starting_a_tile_over_names_the_tile()
    {
        var request = Assert.IsType<TileCommandRequest>(Parse("""{"type":"new","tileId":"t"}"""));
        Assert.Equal("t", request.TileId);
        Assert.IsType<RemoteNewConversation>(request.Command);
        Assert.Null(Parse("""{"type":"new"}"""));
    }

    [Fact]
    public void The_composer_s_own_requests_name_the_tile_and_what_they_carry()
    {
        Assert.Equal(new RemoteDraft("x", "y"),
            Assert.IsType<TileCommandRequest>(Parse("""{"type":"draft","tileId":"t","text":"x","seen":"y"}""")).Command);
        Assert.Equal(new RemoteDraft("", null),
            Assert.IsType<TileCommandRequest>(Parse("""{"type":"draft","tileId":"t","text":""}""")).Command);
        Assert.Equal(new RemotePick("mode", "auto"),
            Assert.IsType<TileCommandRequest>(Parse("""{"type":"pick","tileId":"t","picker":"mode","value":"auto"}""")).Command);
        Assert.IsType<RemoteCompact>(Assert.IsType<TileCommandRequest>(Parse("""{"type":"compact","tileId":"t"}""")).Command);
        Assert.Equal(new RemoteOpenConversation("c"),
            Assert.IsType<TileCommandRequest>(Parse("""{"type":"openConversation","tileId":"t","conversationId":"c"}""")).Command);
        Assert.Equal(new ConversationsRequest("t"), Parse("""{"type":"conversations","tileId":"t"}"""));
        Assert.Null(Parse("""{"type":"pick","tileId":"t","picker":"mode"}"""));
    }

    [Fact]
    public void A_recording_can_ask_to_come_back_to_the_phone_rather_than_into_the_tile()
    {
        var header = PhoneProtocol.ParseAudioHeader("""{"sampleRate":48000,"tileId":"t","toDraft":true}"""u8);
        Assert.Equal(new AudioHeader("t", 48000, ToDraft: true), header);
    }

    [Theory]
    [InlineData("""{"sampleRate":48000,"tileId":"t"}""", 48000, "t")]
    [InlineData("""{"sampleRate":16000}""", 16000, null)]
    [InlineData("""{"sampleRate":44100,"tileId":""}""", 44100, null)]
    public void A_recording_header_says_where_and_at_what_rate(string json, int rate, string? tile)
    {
        var header = PhoneProtocol.ParseAudioHeader(Encoding.UTF8.GetBytes(json));
        Assert.Equal(new AudioHeader(tile, rate), header);
    }

    /// <summary>The rate sizes a resampling kernel and comes from the network: what is not a
    /// microphone is refused rather than trusted.</summary>
    [Theory]
    [InlineData("""{"sampleRate":7999}""")]
    [InlineData("""{"sampleRate":192001}""")]
    [InlineData("""{"sampleRate":"48000"}""")]
    [InlineData("""{"tileId":"t"}""")]
    [InlineData("pcm")]
    public void A_header_that_is_not_a_microphone_is_refused(string json) =>
        Assert.Null(PhoneProtocol.ParseAudioHeader(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void A_push_carries_its_type_beside_its_body()
    {
        using var document = JsonDocument.Parse(PhoneProtocol.Push("tileGone", new { tileId = "t" }));

        Assert.Equal("tileGone", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("t", document.RootElement.GetProperty("tileId").GetString());
    }

    [Fact]
    public void An_error_says_so_in_words()
    {
        using var document = JsonDocument.Parse(PhoneProtocol.Error("nope"));

        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("nope", document.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void A_page_is_served_from_the_oldest_version_up_to_this_one(int pageVersion, bool accepted) =>
        Assert.Equal(accepted, PhoneProtocol.Accepts(pageVersion));
}
