using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>
/// Tier 1 decoder: Windows Imaging Component, reached through WPF's bitmap types.
/// </summary>
/// <remarks>
/// This is the only decoder on the hot path and handles the overwhelming majority of files:
/// JPEG, PNG, GIF, BMP, TIFF, ICO, CUR, DDS and JXR are always available, and HEIC, WebP,
/// JPEG-XL, AVIF and camera RAW are available whenever the corresponding Windows codec is
/// installed. Nothing here references the heavier tiers, so their assemblies stay unloaded.
/// </remarks>
public static class WicDecoder
{
    public const string Name = "WIC";

    /// <summary>
    /// Pulls the embedded thumbnail out of a file, if it has one.
    /// </summary>
    /// <remarks>
    /// JPEG, HEIC and RAW files almost always carry a small preview in their EXIF block. Decoding
    /// it costs a couple of milliseconds against a couple of hundred for a 24 MP frame, so it goes
    /// on screen first and the full decode quietly replaces it. This is what makes navigation feel
    /// instant rather than merely fast.
    /// </remarks>
    public static DecodedImage? TryDecodeEmbeddedThumbnail(
        byte[] bytes, string path, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

            if (decoder.Frames.Count == 0) return null;
            var frame = decoder.Frames[0];

            ct.ThrowIfCancellationRequested();

            // decoder.Thumbnail is the container-level preview; frame.Thumbnail is per-frame.
            // Either will do, whichever the codec chose to expose.
            var thumb = SafeThumbnail(() => frame.Thumbnail) ?? SafeThumbnail(() => decoder.Thumbnail);
            if (thumb is null) return null;

            var orientation = ReadExifOrientation(frame);
            var oriented = ApplyOrientation(thumb, orientation);
            oriented.Freeze();

            var turned = orientation is >= 5 and <= 8;

            return new DecodedImage
            {
                Bitmap = oriented,
                // Report the *frame's* dimensions, not the thumbnail's, so the view can lay the
                // preview out at exactly the position the real image will occupy and swap without
                // a visible jump. Quarter turns swap the displayed axes.
                PixelWidth = turned ? frame.PixelHeight : frame.PixelWidth,
                PixelHeight = turned ? frame.PixelWidth : frame.PixelHeight,
                DecoderName = Name + " (thumb)",
                Path = path,
                FileSizeBytes = bytes.LongLength,
                IsPreview = true,
                AppliedExifOrientation = orientation,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // A missing or malformed thumbnail is entirely normal - fall through to a full decode.
            return null;
        }
    }

    /// <summary>
    /// Fully decodes an image, downscaling during decode to approximately <paramref name="maxWidth"/>
    /// x <paramref name="maxHeight"/> physical pixels.
    /// </summary>
    /// <remarks>
    /// Pass 0 for either bound to decode at full resolution (needed once the user zooms past 100%).
    /// Decoding a 6000 px photo straight down to a 2560 px viewport is roughly three times faster
    /// and uses about five times less memory than decoding full-size and scaling afterwards.
    /// </remarks>
    public static DecodedImage Decode(
        byte[] bytes, string path, int maxWidth, int maxHeight, CancellationToken ct)
    {
        int naturalWidth, naturalHeight, orientation;

        // First pass: header only. DelayCreation + CacheOption.None means no pixels are touched,
        // so this costs microseconds even on a 50 MB RAW file.
        using (var probeStream = new MemoryStream(bytes, writable: false))
        {
            var probe = BitmapDecoder.Create(
                probeStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

            if (probe.Frames.Count == 0)
                throw new InvalidDataException("Image contains no frames.");

            var probeFrame = probe.Frames[0];
            naturalWidth = probeFrame.PixelWidth;
            naturalHeight = probeFrame.PixelHeight;
            orientation = ReadExifOrientation(probeFrame);
        }

        ct.ThrowIfCancellationRequested();

        if (naturalWidth <= 0 || naturalHeight <= 0)
            throw new InvalidDataException("Image reports zero dimensions.");

        // Orientations 5-8 rotate by a quarter turn, so the *displayed* extent has its axes
        // swapped. The fit calculation has to be done against displayed extent, not stored extent.
        var quarterTurned = orientation is >= 5 and <= 8;
        var displayWidth = quarterTurned ? naturalHeight : naturalWidth;
        var displayHeight = quarterTurned ? naturalWidth : naturalHeight;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(bytes, writable: false);
        // OnLoad completes decoding inside EndInit, so the stream can be released immediately
        // and the file is never left locked - which matters for delete and rename.
        bmp.CacheOption = BitmapCacheOption.OnLoad;

        // Only ever constrain ONE axis. Setting both forces an exact size and would distort
        // anything whose aspect ratio differs from the viewport.
        if (maxWidth > 0 && maxHeight > 0 &&
            (displayWidth > maxWidth || displayHeight > maxHeight))
        {
            var scale = Math.Min(maxWidth / (double)displayWidth, maxHeight / (double)displayHeight);

            if (quarterTurned)
            {
                // Decode dimensions refer to stored orientation, so the axis to pin is flipped.
                var target = Math.Max(1, (int)Math.Round(naturalHeight * scale));
                bmp.DecodePixelHeight = target;
            }
            else
            {
                var target = Math.Max(1, (int)Math.Round(naturalWidth * scale));
                bmp.DecodePixelWidth = target;
            }
        }

        bmp.EndInit();

        ct.ThrowIfCancellationRequested();

        BitmapSource result = ApplyOrientation(bmp, orientation);
        result.Freeze();

        return new DecodedImage
        {
            Bitmap = result,
            PixelWidth = displayWidth,
            PixelHeight = displayHeight,
            DecoderName = Name,
            Path = path,
            FileSizeBytes = bytes.LongLength,
            IsPreview = false,
            AppliedExifOrientation = orientation,
        };
    }

    private static BitmapSource? SafeThumbnail(Func<BitmapSource?> get)
    {
        // Several codecs throw rather than returning null when no thumbnail is present.
        try { return get(); }
        catch { return null; }
    }

    /// <summary>
    /// Reads the EXIF orientation tag (0x0112).
    /// </summary>
    /// <remarks>
    /// WIC does <em>not</em> apply this automatically, so without this step every photo taken with
    /// a rotated camera would display on its side. Returns 1 (normal) when absent or unreadable.
    /// </remarks>
    public static int ReadExifOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata meta) return 1;

            // JPEG nests EXIF under APP1; TIFF and most RAW containers expose the IFD directly.
            foreach (var query in (ReadOnlySpan<string>)["/app1/ifd/{ushort=274}", "/ifd/{ushort=274}"])
            {
                object? value = null;
                try { value = meta.GetQuery(query); }
                catch { /* query unsupported by this codec */ }

                if (value is ushort u && u is >= 1 and <= 8) return u;
                if (value is short s && s is >= 1 and <= 8) return s;
            }
        }
        catch
        {
            // Metadata is optional and frequently malformed in the wild; never fail a decode over it.
        }

        return 1;
    }

    /// <summary>
    /// Bakes an EXIF orientation into the pixels.
    /// </summary>
    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        if (orientation is <= 1 or > 8) return source;

        // EXIF orientation combines an optional mirror with a rotation.
        var (angle, mirror) = orientation switch
        {
            2 => (0d, true),
            3 => (180d, false),
            4 => (180d, true),
            5 => (90d, true),
            6 => (90d, false),
            7 => (270d, true),
            8 => (270d, false),
            _ => (0d, false),
        };

        // TransformedBitmap only accepts a single orthogonal ScaleTransform or RotateTransform -
        // handing it a TransformGroup throws. Chaining two TransformedBitmaps is the supported way
        // to combine them, and costs nothing extra: they are evaluated lazily by the render pipeline.
        var current = source;

        if (mirror)
        {
            var flip = new ScaleTransform(-1, 1);
            flip.Freeze();
            current = new TransformedBitmap(current, flip);
        }

        if (angle != 0)
        {
            var rotate = new RotateTransform(angle);
            rotate.Freeze();
            current = new TransformedBitmap(current, rotate);
        }

        current.Freeze();
        return current;
    }
}
