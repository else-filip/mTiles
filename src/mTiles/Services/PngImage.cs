namespace mTiles.Services;

/// <summary>A picture in any format the imaging stack reads, re-encoded as PNG.</summary>
/// <remarks>For the Goal tile's image store, which writes what it is given under <c>.png</c>. Safe off the
/// UI thread.</remarks>
public static class PngImage
{
    public static byte[] From(byte[] image)
    {
        using var input = new MemoryStream(image);
        using var bitmap = new Avalonia.Media.Imaging.Bitmap(input);
        using var png = new MemoryStream();
        bitmap.Save(png);
        return png.ToArray();
    }
}
