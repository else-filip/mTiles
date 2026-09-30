using mTiles.Services.Phone.Remote;

namespace mTiles.ViewModels;

/// <summary>The git tile as a paired phone sees it: the changed files with their ticks, the diff of the
/// one opened, and the commit message as the composer.</summary>
/// <remarks>
/// Only what the header's three actions already allowed a phone — see what changed, record it, send it —
/// now with the list that makes "record it" mean something: which files go in, and what they say. Discard
/// and Undo stay on the computer, as <see cref="Actions"/> already says.
/// </remarks>
public partial class GitTileViewModel : IRemoteViewTile
{
    /// <summary>How much of a diff a phone is sent. A generated file's diff runs to megabytes, and the whole
    /// of it goes out again every time anything in the tile moves.</summary>
    internal const int RemoteDiffLimit = 40_000;

    /// <summary>The changed file <see cref="DiffText"/> was loaded for, or null when it holds a commit's
    /// diff or nothing — the diff arrives after the selection moves, and the History tab writes it too.</summary>
    internal string? DiffFilePath { get; private set; }

    /// <inheritdoc />
    /// <remarks>Computed rather than counted: the list is rebuilt by reconciling, and a tick is a property of
    /// an item rather than of the tile, so no single change notification covers everything drawn.</remarks>
    public long RemoteVersion
    {
        get
        {
            var hash = new HashCode();
            hash.Add(IsGitRepo);
            hash.Add(IsLoading);
            hash.Add(BranchName);
            hash.Add(UnpushedCount);
            hash.Add(CommitMessage);
            hash.Add(SelectedChange?.FilePath);
            hash.Add(DiffText);
            hash.Add(DiffFilePath);
            foreach (var change in Changes)
            {
                hash.Add(change.FilePath);
                hash.Add(change.Status);
                hash.Add(change.IsChecked);
            }
            return hash.ToHashCode();
        }
    }

    /// <inheritdoc />
    public RemoteTileBody DescribeForRemote()
    {
        var status = PreviewForRemote()?.Text;
        if (!IsGitRepo)
            return new RemoteTileBody("list", new RemoteStatus("idle", "This workspace is not a git repository."),
                List: new RemoteList([]));

        var items = Changes.Select(c => new RemoteListItem(
            c.FilePath, c.DisplayPath, null, c.IsChecked, c.Status, Selectable: true,
            Selected: ReferenceEquals(c, SelectedChange))).ToList();
        var section = new RemoteListSection(
            Changes.Count == 1 ? "1 change" : $"{Changes.Count} changes", items,
            CheckAll: Changes.Count > 0 ? Changes.All(c => c.IsChecked) : null,
            Empty: "Nothing to commit.");

        var ticked = Changes.Count(c => c.IsChecked);
        return new RemoteTileBody(
            "list",
            new RemoteStatus(IsLoading ? "working" : "idle", status),
            Composer: CommitComposer(ticked),
            List: new RemoteList([section], OpenedDiff()));
    }

    private RemoteComposer CommitComposer(int ticked) => new(
        true,
        ticked == 0 ? "Tick the files to commit, then write the message" : $"Commit message ({ticked} {(ticked == 1 ? "file" : "files")})",
        Draft: CommitMessage.Length > 0 ? CommitMessage : null,
        SyncsDraft: true);

    /// <summary>The diff of the opened file — only once the diff on hand is that file's own.</summary>
    private RemoteDetail? OpenedDiff() =>
        SelectedChange is { } selected && DiffFilePath == selected.FilePath && DiffText.Length > 0
            ? new RemoteDetail(selected.DisplayPath, CutForRemote(DiffText), "diff")
            : null;

    internal static string CutForRemote(string diff) =>
        diff.Length > RemoteDiffLimit ? diff[..RemoteDiffLimit] + "\n… (the rest is on the computer)" : diff;

    /// <inheritdoc />
    public async Task<RemoteRefusal?> HandleRemoteAsync(RemoteTileCommand command)
    {
        switch (command)
        {
            case RemoteItem { Act: "check", ItemId: "*" }:
                var all = !Changes.All(c => c.IsChecked);
                foreach (var change in Changes) change.IsChecked = all;
                CommitCommand.NotifyCanExecuteChanged();
                return null;

            case RemoteItem { Act: "check" } check:
                if (Changes.FirstOrDefault(c => c.FilePath == check.ItemId) is not { } ticked)
                    return "That file is no longer changed.";
                ticked.IsChecked = !ticked.IsChecked;
                CommitCommand.NotifyCanExecuteChanged();
                return null;

            case RemoteItem { Act: "select" } select:
                if (Changes.FirstOrDefault(c => c.FilePath == select.ItemId) is not { } opened)
                    return "That file is no longer changed.";
                if (ReferenceEquals(SelectedChange, opened)) await LoadDiffForSelectedAsync();
                else SelectedChange = opened;
                return null;

            case RemoteDraft typed:
                if (RemoteText.WouldOverwrite(CommitMessage, typed.Text, typed.Seen)) return RemoteText.DraftInTheWay;
                CommitMessage = typed.Text;
                return null;

            case RemoteSendText send:
                if (RemoteText.WouldOverwrite(CommitMessage, send.Text, send.Replaces)) return RemoteText.DraftInTheWay;
                CommitMessage = send.Text.Trim();
                if (!send.Submit) return null;
                if (IsLoading) return "The tile is busy. Try again in a moment.";
                return await CommitCheckedAsync();

            default:
                return "The git tile cannot do that from a phone.";
        }
    }
}
