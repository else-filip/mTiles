using mTiles.Services.Phone.Remote;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>What a paired phone sees of a git tile and may do to it: the changed files with their ticks,
/// the diff of the one opened, and the commit message as its composer.</summary>
/// <remarks>Driven inside one UI-thread body: the tile starts a refresh of its own, and nothing it awaits
/// can run until the body has finished asking.</remarks>
public class GitRemoteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-git-remote-" + Guid.NewGuid());

    public GitRemoteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
    }

    [Fact]
    public void The_changed_files_are_a_list_with_their_ticks()
    {
        Ui.Run(() =>
        {
            using var tile = TileWith(("a.cs", "M", true), ("b.cs", "?", false));

            var list = Assert.IsType<RemoteList>(tile.DescribeForRemote().List);
            var items = Assert.Single(list.Sections).Items;
            Assert.Equal(["a.cs", "b.cs"], items.Select(i => i.Id));
            Assert.Equal([true, false], items.Select(i => i.Checked));
            Assert.All(items, i => Assert.True(i.Selectable));
            Assert.False(list.Sections[0].CheckAll);
        });
    }

    [Fact]
    public void A_tick_from_the_phone_flips_one_file_or_all_of_them()
    {
        Ui.Run(() =>
        {
            using var tile = TileWith(("a.cs", "M", true), ("b.cs", "?", false));

            Assert.Null(Handle(tile, new RemoteItem("b.cs", "check")));
            Assert.True(tile.Changes.All(c => c.IsChecked));
            Assert.Null(Handle(tile, new RemoteItem("*", "check")));
            Assert.True(tile.Changes.All(c => !c.IsChecked));
            Assert.Equal("That file is no longer changed.", Handle(tile, new RemoteItem("gone.cs", "check"))?.Message);
        });
    }

    [Fact]
    public void The_commit_message_is_the_composer_and_a_commit_needs_a_tick()
    {
        Ui.Run(() =>
        {
            using var tile = TileWith(("a.cs", "M", false));

            Assert.Null(Handle(tile, new RemoteDraft("fix the thing", null)));
            Assert.Equal("fix the thing", tile.CommitMessage);
            Assert.Equal("fix the thing", tile.DescribeForRemote().Composer?.Draft);
            Assert.Equal("Tick the files to commit first.",
                Handle(tile, new RemoteSendText("fix the thing", Submit: true))?.Message);
        });
    }

    [Fact]
    public void A_diff_is_shown_only_under_the_file_it_was_loaded_for()
    {
        Ui.Run(() =>
        {
            using var tile = TileWith(("a.cs", "M", true));
            tile.SelectedChange = tile.Changes[0];
            tile.DiffText = "+a commit's line";     // as the History tab leaves it: no file of its own

            Assert.Null(tile.DescribeForRemote().List?.Detail);
        });
    }

    [Fact]
    public void A_long_diff_is_cut_for_the_phone()
    {
        var diff = new string('x', GitTileViewModel.RemoteDiffLimit + 10);

        var cut = GitTileViewModel.CutForRemote(diff);

        Assert.StartsWith(diff[..GitTileViewModel.RemoteDiffLimit], cut);
        Assert.EndsWith("(the rest is on the computer)", cut);
        Assert.Equal("short", GitTileViewModel.CutForRemote("short"));
    }

    [Fact]
    public void A_commit_with_no_message_is_refused_rather_than_reported_done()
    {
        Ui.Run(() =>
        {
            using var tile = TileWith(("a.cs", "M", true));

            Assert.Equal("Write a commit message first.",
                Handle(tile, new RemoteSendText("   ", Submit: true))?.Message);
        });
    }

    private static RemoteRefusal? Handle(GitTileViewModel tile, RemoteTileCommand command) =>
        tile.HandleRemoteAsync(command).GetAwaiter().GetResult();

    private GitTileViewModel TileWith(params (string Path, string Status, bool Ticked)[] files)
    {
        var tile = new GitTileViewModel(_dir);
        tile.Changes.Clear();
        foreach (var (path, status, ticked) in files)
            tile.Changes.Add(new Models.GitFileChange { FilePath = path, Status = status, StatusDisplay = status, IsChecked = ticked });
        return tile;
    }
}
