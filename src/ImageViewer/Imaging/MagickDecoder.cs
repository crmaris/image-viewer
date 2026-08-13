using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace ImageViewer.Imaging;

/// <summary>
/// Tier 4: Magick.NET, the last-resort catch-all.
/// </summary>
/// <remarks>
/// <para>
/// Reached only when WIC and ImageSharp have both declined a file. In exchange it opens PSD, XCF,
/// EXR, JPEG 2000, PCX, Radiance HDR and a few hundred other formats, plus camera RAW on machines
/// without the Microsoft Raw Image Extension.
/// </para>
/// <para>
/// It is also the most expensive thing this application can load: roughly 40 MB of native
/// ImageMagick, with a first-use initialisation cost of 100-200 ms. That is acceptable precisely
/// because two cheaper tiers run first, so the cost is paid only by genuinely exotic files, and
/// never at startup - every ImageMagick reference is confined to non-inlined method bodies.
/// </para>
/// </remarks>
public static class MagickDecoder
{
    public const string Name = "Magick.NET";

    /// <summary>
    /// Deliberately permissive: this tier only runs after the others have failed, so the useful
    /// answer to "can you try?" is almost always yes.
    /// </summary>
    public static bool Supports(ImageFormatKind kind) => true;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static DecodedImage Decode(
        byte[] bytes, string path, int maxWidth, int maxHeight, CancellationToken ct)
    {
        using var image = new MagickImage();
        image.Read(bytes);

        ct.ThrowIfCancellationRequested();

        // Bake in any EXIF orientation, matching the other tiers.
        image.AutoOrient();

        var naturalWidth = (int)image.Width;
        var naturalHeight = (int)image.Height;

        if (maxWidth > 0 && maxHeight > 0 && (naturalWidth > maxWidth || naturalHeight > maxHeight))
        {
            var scale = Math.Min(maxWidth / (double)naturalWidth, maxHeight / (double)naturalHeight);
            var targetW = (uint)Math.Max(1, (int)Math.Round(naturalWidth * scale));
            var targetH = (uint)Math.Max(1, (int)Math.Round(naturalHeight * scale));

            // Resize rather than Thumbnail: Thumbnail strips profiles, which would throw away the
            // colour information the viewer needs to render accurately.
            image.Resize(targetW, targetH);
        }

        ct.ThrowIfCancellationRequested();

        // Flatten layered formats (PSD, multi-layer TIFF) onto their composite. Without this a PSD
        // shows only its first layer.
        image.Alpha(AlphaOption.Set);

        var width = (int)image.Width;
        var height = (int)image.Height;
        var stride = width * 4;

        using var pixels = image.GetPixels();
        var buffer = pixels.ToByteArray(PixelMapping.BGRA)
            ?? throw new InvalidOperationException("Magick.NET returned no pixel data.");

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, palette: null, buffer, stride);
        bitmap.Freeze();

        return new DecodedImage
        {
            Bitmap = bitmap,
            PixelWidth = naturalWidth,
            PixelHeight = naturalHeight,
            DecoderName = Name,
            Path = path,
            FileSizeBytes = bytes.LongLength,
            IsPreview = false,
        };
    }
}
