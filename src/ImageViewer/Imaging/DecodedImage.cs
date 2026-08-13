using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>
/// A decoded, frozen image ready to hand to the UI thread.
/// </summary>
/// <remarks>
/// <see cref="Bitmap"/> may be a <em>downscaled</em> render of the file: the decoder is asked for
/// roughly display resolution rather than full resolution, which is the single biggest decode
/// speed-up for large photos. <see cref="PixelWidth"/>/<see cref="PixelHeight"/> therefore always
/// report the <em>original</em> dimensions, so the info overlay and the 100% zoom level stay
/// truthful regardless of what was actually decoded.
/// </remarks>
public sealed class DecodedImage
{
    /// <summary>Frozen bitmap, safe to pass between threads without copying.</summary>
    public required BitmapSource Bitmap { get; init; }

    /// <summary>Width of the image as stored in the file, before any downscaling.</summary>
    public required int PixelWidth { get; init; }

    /// <summary>Height of the image as stored in the file, before any downscaling.</summary>
    public required int PixelHeight { get; init; }

    /// <summary>Which decoder tier produced this, for the info overlay and diagnostics.</summary>
    public required string DecoderName { get; init; }

    /// <summary>Absolute path this was decoded from.</summary>
    public required string Path { get; init; }

    /// <summary>Size of the file on disk, in bytes.</summary>
    public long FileSizeBytes { get; init; }

    /// <summary>
    /// True when this is a fast placeholder (an embedded EXIF thumbnail) that a full decode is
    /// expected to replace. The UI uses this to avoid caching it as if it were the real thing.
    /// </summary>
    public bool IsPreview { get; init; }

    /// <summary>
    /// EXIF orientation already baked into <see cref="Bitmap"/>, recorded so a later save knows
    /// the pixels no longer match the file's original orientation tag.
    /// </summary>
    public int AppliedExifOrientation { get; init; } = 1;

    /// <summary>
    /// Ratio of decoded pixels to original pixels (1.0 = decoded at full resolution).
    /// The view uses this to convert a requested zoom level into a render scale.
    /// </summary>
    public double DecodeScale =>
        PixelWidth > 0 ? Bitmap.PixelWidth / (double)PixelWidth : 1.0;

    /// <summary>Approximate memory footprint, used by the LRU cache's budget.</summary>
    public long ApproximateBytes =>
        (long)Bitmap.PixelWidth * Bitmap.PixelHeight * ((Bitmap.Format.BitsPerPixel + 7) / 8);
}
