using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using mTiles.Services;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>A photo or a file sent from a phone into a Goal tile: a picture is stored as PNG and named by its
/// marker, and something that is not a picture at all is refused in words.</summary>
public class GoalRemoteAttachTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-goal-attach-" + Guid.NewGuid());

    public GoalRemoteAttachTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
    }

    [Fact]
    public void A_picture_is_stored_as_png_and_answered_with_its_marker() => Ui.Run(async () =>
    {
        using var vm = Tile();

        var result = await vm.AttachFromRemoteAsync("photo.png", "image/png", APicture());

        Assert.NotNull(result.Marker);
        Assert.Equal("", vm.InputText);
        // The headless renderer writes no real pixels, so what is pinned is that the store took it as PNG.
        Assert.Single(Directory.GetFiles(_dir, "*.png", SearchOption.AllDirectories));
    });

    [Fact]
    public async Task Bytes_that_are_no_picture_are_refused_in_words()
    {
        using var vm = Tile();

        var result = await vm.AttachFromRemoteAsync("photo.jpg", "image/jpeg", [1, 2, 3]);

        Assert.Null(result.Marker);
        Assert.StartsWith("The image could not be saved", result.Notice);
    }

    [Fact]
    public async Task A_file_is_named_by_a_mention()
    {
        using var vm = Tile();

        var result = await vm.AttachFromRemoteAsync("notes.txt", "text/plain", "hello"u8.ToArray());

        Assert.StartsWith("@.mtiles/attachments/", result.Marker);
        Assert.True(vm.DescribeForRemote().Composer?.TakesAttachments);
    }

    private GoalTileViewModel Tile() =>
        new(_dir, new SettingsService(Path.Combine(_dir, "settings.json"))) { ConfirmAction = _ => Task.FromResult(true) };

    private static byte[] APicture()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
        using var png = new MemoryStream();
        bitmap.Save(png);
        return png.ToArray();
    }
}
