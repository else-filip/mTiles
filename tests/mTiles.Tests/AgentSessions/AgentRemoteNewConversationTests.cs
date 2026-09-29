using mTiles.Services.Phone.Remote;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>A new conversation asked for from the phone was asked about on the phone; from the computer it
/// is asked about on the computer. Swapped, the phone waits on a dialog nobody at it can see, or the
/// desktop button stops asking.</summary>
public class AgentRemoteNewConversationTests
{
    [Fact]
    public async Task The_phone_starts_a_new_conversation_without_the_computers_question()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);
        var asked = 0;
        vm.ConfirmAction = _ => { asked++; return Task.FromResult(false); };
        var before = vm.ConversationId;

        Assert.Null(await vm.HandleRemoteAsync(new RemoteNewConversation()));

        await ConversationTiles.WaitUntil(() => vm.ConversationId != before, "the tile to move to a new conversation");
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task The_computers_button_still_asks_and_a_no_keeps_the_conversation()
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);
        var asked = 0;
        vm.ConfirmAction = _ => { asked++; return Task.FromResult(false); };
        var before = vm.ConversationId;

        await vm.NewConversationCommand.ExecuteAsync(null);

        Assert.Equal(1, asked);
        Assert.Equal(before, vm.ConversationId);
    }
}
