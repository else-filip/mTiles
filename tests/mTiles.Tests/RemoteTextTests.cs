using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

public class RemoteTextTests
{
    [Theory]
    [InlineData(null, "hi", false)]
    [InlineData("", "hi", false)]
    [InlineData("  ", "hi", false)]
    [InlineData("hi", "hi", false)]
    [InlineData("half a message", "hi", true)]
    public void A_draft_typed_on_the_computer_is_never_replaced(string? draft, string incoming, bool refused) =>
        Assert.Equal(refused, RemoteText.WouldOverwrite(draft, incoming));
}
