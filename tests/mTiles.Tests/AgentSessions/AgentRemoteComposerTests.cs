using mTiles.Services.Phone.Remote;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>What the phone's text box and its pickers do to an Agent tile's own composer.</summary>
public class AgentRemoteComposerTests
{
    [Fact]
    public async Task What_is_typed_on_the_phone_is_mirrored_into_the_tiles_box()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);

        Assert.Null(await vm.HandleRemoteAsync(new RemoteDraft("half a", Seen: null)));
        Assert.Null(await vm.HandleRemoteAsync(new RemoteDraft("half a sentence", Seen: "half a")));

        Assert.Equal("half a sentence", vm.Draft);
        Assert.Equal("half a sentence", vm.DescribeForRemote().Composer?.Draft);
        Assert.True(vm.DescribeForRemote().Composer?.SyncsDraft);
    }

    [Fact]
    public async Task A_draft_typed_on_the_computer_meanwhile_is_not_written_over()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);
        vm.Draft = "typed at the desk";

        Assert.Equal(RemoteText.DraftInTheWay, await vm.HandleRemoteAsync(new RemoteDraft("from the phone", Seen: "older")));
        Assert.Equal("typed at the desk", vm.Draft);
    }

    [Fact]
    public async Task A_setting_the_tile_does_not_offer_is_refused()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);

        Assert.Equal("That is not on offer any more.", await vm.HandleRemoteAsync(new RemotePick("mode", "nonsense")));
        Assert.Equal("That is not on offer any more.", await vm.HandleRemoteAsync(new RemotePick("colour", "red")));
    }

    [Fact]
    public async Task Compact_is_refused_where_the_agent_has_no_route_for_it()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);

        Assert.False(vm.DescribeForRemote().Composer?.CanCompact);
        Assert.NotNull(await vm.HandleRemoteAsync(new RemoteCompact()));
    }

    [Fact]
    public async Task The_open_conversation_is_always_in_the_list()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);

        var list = await vm.ConversationsForRemoteAsync();

        Assert.Single(list, c => c.Current);
        Assert.Equal("That conversation is no longer there.",
            await vm.HandleRemoteAsync(new RemoteOpenConversation("not-a-conversation")));
    }
}
