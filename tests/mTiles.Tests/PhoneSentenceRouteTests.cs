using mTiles.Services.Phone;
using Xunit;

namespace mTiles.Tests;

/// <summary>A sentence the phone asked for in its own box is handed back and typed into nothing here — or
/// it would reach the tile twice — and is sent at once only under the phone's auto-Enter.</summary>
public class PhoneSentenceRouteTests
{
    [Theory]
    [InlineData(true, false, true, null, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, false, null, null, true)]
    [InlineData(false, true, null, null, true)]
    public void Where_a_dictated_sentence_goes(bool toDraft, bool autoEnter, bool? draft, bool? send, bool typedIntoTile)
    {
        var route = PhoneDictation.SentenceRoute.For(toDraft, autoEnter);

        Assert.Equal(draft, route.Draft);
        Assert.Equal(send, route.Send);
        Assert.Equal(typedIntoTile, route.TypedIntoTile);
    }
}
