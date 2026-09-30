using mTiles.ViewModels;

namespace mTiles.Services;

/// <summary>
/// Where a file sent from a phone is written: beside the workspace's other attachments, under its own name.
/// </summary>
/// <remarks>
/// The same directory and the same free-name rule a file dropped from outside the workspace is copied
/// under (<see cref="AttachmentStore"/>), so the composer names it exactly as it names a copy — relative to
/// the workspace, handed over to be read. Written off the caller's thread: a phone's file can be twenty
/// megabytes.
/// </remarks>
public static class RemoteAttachmentFile
{
    private static readonly HashSet<string> AgentImageTypes =
        new(["image/png", "image/jpeg", "image/gif", "image/webp"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a picture of this type can go to an agent as an image. Any other picture — an SVG, a
    /// HEIC the phone could not decode — would fail the whole turn, so it goes as a file instead.</summary>
    public static bool IsAgentImage(string mimeType) => AgentImageTypes.Contains(mimeType);

    /// <summary>Writes the file and answers the <c>@</c> mention that names it.</summary>
    public static async Task<RemoteAttachResult> AttachAsync(string name, byte[] data, string workspaceDirectory)
    {
        var path = await SaveAsync(name, data, workspaceDirectory);
        var (mention, notice) = await ComposerFileReference.ForAsync(path, workspaceDirectory);
        return new RemoteAttachResult(mention, notice);
    }

    private static Task<string> SaveAsync(string name, byte[] data, string workspaceDirectory) =>
        Task.Run(async () =>
        {
            var directory = AttachmentStore.CopiesDirectory(workspaceDirectory);
            Directory.CreateDirectory(directory);
            var path = AttachmentStore.FreePath(directory, name);
            await File.WriteAllBytesAsync(path, data).ConfigureAwait(false);
            return path;
        });
}
