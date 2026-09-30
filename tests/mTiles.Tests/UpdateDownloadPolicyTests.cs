using mTiles.Services;
using Velopack;
using Xunit;

namespace mTiles.Tests;

/// <summary>The waiting update is always the newest release, and nothing is fetched twice.</summary>
public class UpdateDownloadPolicyTests
{
    [Theory]
    [InlineData(null, null, false)]         // nothing found
    [InlineData(null, "0.4.155", false)]    // nothing found, one waiting
    [InlineData("0.4.155", null, true)]     // first release found
    [InlineData("0.4.156", "0.4.155", true)] // newer than the waiting one replaces it
    [InlineData("0.4.155", "0.4.155", false)] // the package already on disk
    [InlineData("0.4.154", "0.4.155", false)] // never a step backwards
    public void Only_a_release_newer_than_the_waiting_one_is_downloaded(string? found, string? waiting, bool expected)
    {
        Assert.Equal(expected, UpdateDownloadPolicy.ShouldDownload(Parse(found), Parse(waiting)));
    }

    private static SemanticVersion? Parse(string? version) => version is null ? null : SemanticVersion.Parse(version);
}
