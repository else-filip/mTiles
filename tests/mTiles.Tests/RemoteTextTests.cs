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

    /// <summary>A draft the phone was shown — and took into its own box — is the phone's to replace; a
    /// different one, typed on the computer since, still is not.</summary>
    [Theory]
    [InlineData("dictated on the computer", "dictated on the computer, edited", "dictated on the computer", false)]
    [InlineData("typed since", "edited", "dictated on the computer", true)]
    [InlineData("typed since", "edited", null, true)]
    public void A_draft_the_phone_was_shown_may_be_replaced(string draft, string incoming, string? seen, bool refused) =>
        Assert.Equal(refused, RemoteText.WouldOverwrite(draft, incoming, seen));
}
