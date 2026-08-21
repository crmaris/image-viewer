using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer.Cli;

/// <summary>
/// Encodes a decoded bitmap to a file, choosing the encoder from the target extension.
/// </summary>
/// <remarks>
/// The viewer itself only ever needed to write a file back in the format it was already in, which
/// <see cref="Editing.ImageSaver"/> handles. Converting between formats is a CLI-only concern, so
/// it lives here rather than being bolted onto the display path.
/// </remarks>
internal static class ImageWriter
{
    /// <summary>Extensions WPF's own encoders cover, which is every common target.</summary>
    private static readonly HashSet<string> NativeExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib",
            ".tif", ".tiff", ".gif", ".wdp", ".jxr", ".hdp",
        };

    internal static bool IsNativeTarget(string path) =>
        NativeExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Writes <paramref name="image"/> to <paramref name="path"/>.
    /// </summary>
    /// <param name="quality">JPEG quality, 1-100. Ignored by every other format.</param>
    /// <remarks>
    /// Through a temporary file and a move, for the same reason the viewer's own save does it: a
    /// half-written file that had replaced a good one would be the worst possible outcome of a
    /// batch conversion, and converting a file in place is something people legitimately do.
    /// </remarks>
    internal static void Write(BitmapSource image, string path, int quality)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var temporary = path + ".tmp";

        try
        {
            if (IsNativeTarget(path)) WriteNative(image, temporary, path, quality);
            else WriteWithMagick(image, temporary, path);

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void WriteNative(BitmapSource image, string temporary, string path, int quality)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" =>
                new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            ".png" => new PngBitmapEncoder(),
            ".bmp" or ".dib" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            ".wdp" or ".jxr" or ".hdp" => new WmpBitmapEncoder(),
            _ => throw new NotSupportedException($"No encoder for {Path.GetExtension(path)}."),
        };

        // JPEG and GIF cannot carry an alpha channel. Handing them a bitmap that has one gives
        // either a hard failure or black where the transparency was, so it is composited onto white
        // first - the same thing an image editor does when flattening to JPEG.
        var source = encoder is JpegBitmapEncoder or GifBitmapEncoder ? Flatten(image) : image;

        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = File.Create(temporary);
        encoder.Save(stream);
    }

    /// <summary>Composites a possibly-transparent bitmap onto white.</summary>
    private static BitmapSource Flatten(BitmapSource image)
    {
        if (!HasAlpha(image)) return image;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            context.DrawRectangle(Brushes.White, null, bounds);
            context.DrawImage(image, bounds);
        }

        var target = new RenderTargetBitmap(
            image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();

        return target;
    }

    private static bool HasAlpha(BitmapSource image) =>
        image.Format == PixelFormats.Bgra32 ||
        image.Format == PixelFormats.Pbgra32 ||
        image.Format == PixelFormats.Rgba64 ||
        image.Format == PixelFormats.Prgba64 ||
        image.Format == PixelFormats.Rgba128Float ||
        image.Format == PixelFormats.Prgba128Float;

    /// <summary>
    /// Encodes a format WPF cannot write, by handing the raw pixels to Magick.NET.
    /// </summary>
    /// <remarks>
    /// <see cref="MethodImplOptions.NoInlining"/> for the same reason the decoder tiers use it: the
    /// CLR loads an assembly when it JITs a method that mentions it, so inlining this into its
    /// caller would drag 40 MB of ImageMagick into every conversion, including the ones WPF handles
    /// perfectly well by itself.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteWithMagick(BitmapSource image, string temporary, string path)
    {
        BitmapSource converted = image;
        if (image.Format != PixelFormats.Bgra32)
        {
            var formatted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            formatted.Freeze();
            converted = formatted;
        }

        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var settings = new ImageMagick.PixelReadSettings(
            (uint)converted.PixelWidth, (uint)converted.PixelHeight,
            ImageMagick.StorageType.Char, ImageMagick.PixelMapping.BGRA);

        using var magick = new ImageMagick.MagickImage(pixels, settings);
        magick.Write(temporary, MagickFormatFor(path));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ImageMagick.MagickFormat MagickFormatFor(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');

        return Enum.TryParse<ImageMagick.MagickFormat>(extension, ignoreCase: true, out var format)
            ? format
            : throw new NotSupportedException(
                $"{Path.GetExtension(path)} is not a format this build can write.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* a leftover .tmp file is untidy, not harmful */ }
    }
}
