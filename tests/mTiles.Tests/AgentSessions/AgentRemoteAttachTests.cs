using mTiles.Services.Phone.Remote;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>A photo or a file sent from a phone into an Agent tile: kept for the next message and named by
/// the marker the phone puts into its own box — never typed into the tile's box as well.</summary>
public class AgentRemoteAttachTests
{
    [Fact]
    public async Task A_photo_is_kept_and_answered_with_its_marker_without_touching_the_draft()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);
        vm.Draft = "look";

        var result = await vm.AttachFromRemoteAsync("photo.png", "image/png", [1, 2, 3]);

        Assert.Equal("[Image #1]", result.Marker);
        Assert.Equal("look", vm.Draft);
        vm.Draft = "look [Image #1]";
        Assert.Single(vm.Attachments.Items);
    }

    [Fact]
    public async Task A_file_is_written_beside_the_workspace_s_attachments_and_named_by_a_mention()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);

        var result = await vm.AttachFromRemoteAsync("notes.txt", "text/plain", "hello"u8.ToArray());

        Assert.StartsWith("@.mtiles/attachments/", result.Marker);
        Assert.EndsWith("notes.txt", result.Marker);
        Assert.True(vm.DescribeForRemote().Composer?.TakesAttachments);
    }

    [Fact]
    public async Task A_photo_whose_marker_has_not_arrived_counts_against_the_limit()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);
        vm.Clock = new ManualClock();

        for (var i = 0; i < AgentConversationTileViewModel.MaxImages; i++)
            Assert.NotNull((await vm.AttachFromRemoteAsync("p.png", "image/png", [1])).Marker);

        var refused = await vm.AttachFromRemoteAsync("p.png", "image/png", [1]);
        Assert.Null(refused.Marker);
        Assert.NotNull(refused.Notice);
    }

    [Fact]
    public async Task A_marker_that_never_arrives_stops_counting_after_a_minute()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);
        var clock = new ManualClock();
        vm.Clock = clock;
        for (var i = 0; i < AgentConversationTileViewModel.MaxImages; i++)
            await vm.AttachFromRemoteAsync("p.png", "image/png", [1]);

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.NotNull((await vm.AttachFromRemoteAsync("p.png", "image/png", [1])).Marker);
    }

    [Fact]
    public async Task A_marker_that_arrives_is_counted_by_its_chip_and_not_twice()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);
        vm.Clock = new ManualClock();
        var marker = (await vm.AttachFromRemoteAsync("p.png", "image/png", [1])).Marker;

        vm.Draft = marker!;
        for (var i = 1; i < AgentConversationTileViewModel.MaxImages; i++)
            Assert.NotNull((await vm.AttachFromRemoteAsync("p.png", "image/png", [1])).Marker);
    }
}
