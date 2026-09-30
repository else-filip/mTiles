using Velopack;

namespace mTiles.Services;

/// <summary>
/// Whether a release the update check found is worth fetching, given the one already waiting to be
/// applied. Pure, so the rule is argued in a table test rather than trusted to a comparison inline.
/// </summary>
public static class UpdateDownloadPolicy
{
    /// <summary>
    /// Only a release newer than the waiting one: an equal one is the package already on disk, fetched
    /// again every check, and an older one would replace the newest with a step backwards.
    /// </summary>
    public static bool ShouldDownload(SemanticVersion? found, SemanticVersion? waiting) =>
        found is not null && (waiting is null || found > waiting);
}
