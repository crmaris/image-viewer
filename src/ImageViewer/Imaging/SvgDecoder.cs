using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace ImageViewer.Imaging;

/// <summary>
/// Tier 3: SVG rasterisation via Svg.Skia.
/// </summary>
/// <remarks>
/// Vectors have no natural pixel size, so they are rasterised to fill the current viewport rather
/// than to any intrinsic dimension. That is also why <see cref="RasterizeAt"/> exists: zooming into
/// a bitmap just magnifies pixels, but a vector can be re-rendered sharp at the new scale.
/// SkiaSharp types stay inside non-inlined method bodies so the native library is not loaded
/// unless an SVG is actually opened.
/// </remarks>
public static class SvgDecoder
{
    public const string Name = "Svg.Skia";

    /// <summary>Fallback raster size when a vector declares no usable dimensions.</summary>
    private const int DefaultExtent = 1024;

    /// <summary>Ceiling on rasterisation, to stop a deep zoom allocating an enormous surface.</summary>
    private const int MaxExtent = 8192;

    public static bool Supports(ImageFormatKind kind) => kind == ImageFormatKind.Svg;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static DecodedImage Decode(
        byte[] bytes, string path, int maxWidth, int maxHeight, CancellationToken ct)
    {
        using var svg = LoadSvg(bytes, path);

        if (svg.Picture is null)
            throw new InvalidDataException("The SVG could not be parsed.");

        var bounds = svg.Picture.CullRect;
        var naturalWidth = bounds.Width > 0 ? bounds.Width : DefaultExtent;
        var naturalHeight = bounds.Height > 0 ? bounds.Height : DefaultExtent;

        ct.ThrowIfCancellationRequested();

        // Render to fill the viewport. Unlike a bitmap, enlarging a vector loses nothing, so a
        // small icon is rendered up to the window rather than pinned to its declared size.
        var scale = maxWidth > 0 && maxHeight > 0
            ? Math.Min(maxWidth / naturalWidth, maxHeight / naturalHeight)
            : 1.0f;

        var targetW = Math.Clamp((int)Math.Round(naturalWidth * scale), 1, MaxExtent);
        var targetH = Math.Clamp((int)Math.Round(naturalHeight * scale), 1, MaxExtent);

        var bitmap = Render(svg.Picture, naturalWidth, naturalHeight, targetW, targetH);

        return new DecodedImage
        {
            Bitmap = bitmap,
            // Report the rendered extent as the natural size: for a vector these are the same
            // thing, and it keeps the zoom percentage honest against what is on screen.
            PixelWidth = targetW,
            PixelHeight = targetH,
            DecoderName = Name,
            Path = path,
            FileSizeBytes = bytes.LongLength,
            IsPreview = false,
        };
    }

    /// <summary>
    /// Re-renders a vector at a specific pixel size, for a sharp result after zooming in.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static DecodedImage RasterizeAt(
        byte[] bytes, string path, int width, int height, CancellationToken ct) =>
        Decode(bytes, path, Math.Min(width, MaxExtent), Math.Min(height, MaxExtent), ct);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Svg.Skia.SKSvg LoadSvg(byte[] bytes, string path)
    {
        var svg = new Svg.Skia.SKSvg();

        // .svgz is gzip-compressed SVG; the parser needs it decompressed first.
        var isCompressed =
            path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase) ||
            (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B);

        using var raw = new MemoryStream(bytes, writable: false);

        if (isCompressed)
        {
            using var gz = new System.IO.Compression.GZipStream(
                raw, System.IO.Compression.CompressionMode.Decompress);
            using var expanded = new MemoryStream();
            gz.CopyTo(expanded);
            expanded.Position = 0;
            svg.Load(expanded);
        }
        else
        {
            svg.Load(raw);
        }

        return svg;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BitmapSource Render(
        SKPicture picture, float naturalWidth, float naturalHeight, int targetW, int targetH)
    {
        var info = new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        // Transparent rather than white: an SVG logo dropped on the viewer's black background
        // should show through, not sit in a white box.
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(targetW / naturalWidth, targetH / naturalHeight);
        canvas.DrawPicture(picture);
        canvas.Flush();

        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();

        var stride = targetW * 4;
        var buffer = new byte[(long)stride * targetH];
        System.Runtime.InteropServices.Marshal.Copy(pixmap.GetPixels(), buffer, 0, buffer.Length);

        // Skia's Premul alpha maps to WPF's Pbgra32, not Bgra32; using the wrong one makes
        // semi-transparent edges render too bright.
        var bitmap = BitmapSource.Create(
            targetW, targetH, 96, 96, PixelFormats.Pbgra32, palette: null, buffer, stride);

        bitmap.Freeze();
        return bitmap;
    }
}
