using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageViewer.Imaging;

/// <summary>
/// Tier 2: SixLabors.ImageSharp, a fully managed fallback.
/// </summary>
/// <remarks>
/// <para>
/// Exists so the everyday formats work on a machine with no Windows codec extensions installed -
/// a clean Windows install cannot open WebP, and this covers that without asking the user to visit
/// the Store. It also rescues files whose WIC codec rejects them for being slightly malformed,
/// which ImageSharp is generally more tolerant of.
/// </para>
/// <para>
/// Every ImageSharp type is confined to method bodies marked <see cref="MethodImplOptions.NoInlining"/>.
/// The CLR loads an assembly when it JITs a method that references it, so keeping these references
/// out of fields, constructors and signatures is what stops the package loading during a normal
/// JPEG launch.
/// </para>
/// </remarks>
public static class ImageSharpDecoder
{
    public const string Name = "ImageSharp";

    /// <summary>Formats worth attempting here.</summary>
    public static bool Supports(ImageFormatKind kind) => kind switch
    {
        ImageFormatKind.Jpeg or ImageFormatKind.Png or ImageFormatKind.Gif or
        ImageFormatKind.Bmp or ImageFormatKind.Tiff or ImageFormatKind.Webp or
        ImageFormatKind.Targa or ImageFormatKind.Qoi or ImageFormatKind.Pnm => true,
        _ => false,
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static DecodedImage Decode(
        byte[] bytes, string path, int maxWidth, int maxHeight, CancellationToken ct)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var image = Image.Load<Bgra32>(ms);

        var naturalWidth = image.Width;
        var naturalHeight = image.Height;

        ct.ThrowIfCancellationRequested();

        // ImageSharp applies EXIF orientation itself via AutoOrient, unlike WIC.
        image.Mutate(x => x.AutoOrient());

        var orientedWidth = image.Width;
        var orientedHeight = image.Height;

        // Downscale to roughly the requested box, matching the WIC tier's decode-to-fit behaviour.
        if (maxWidth > 0 && maxHeight > 0 &&
            (orientedWidth > maxWidth || orientedHeight > maxHeight))
        {
            var scale = Math.Min(maxWidth / (double)orientedWidth, maxHeight / (double)orientedHeight);
            var targetW = Math.Max(1, (int)Math.Round(orientedWidth * scale));
            var targetH = Math.Max(1, (int)Math.Round(orientedHeight * scale));
            image.Mutate(x => x.Resize(targetW, targetH));
        }

        ct.ThrowIfCancellationRequested();

        var bitmap = ToBitmapSource(image);

        return new DecodedImage
        {
            Bitmap = bitmap,
            PixelWidth = orientedWidth,
            PixelHeight = orientedHeight,
            DecoderName = Name,
            Path = path,
            FileSizeBytes = bytes.LongLength,
            IsPreview = false,
        };
    }

    /// <summary>Copies ImageSharp's pixel buffer into a frozen WPF bitmap.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BitmapSource ToBitmapSource(Image<Bgra32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var stride = width * 4;
        var buffer = new byte[(long)stride * height];

        // Bgra32 matches WPF's Bgra32 byte-for-byte, so this is a straight copy with no conversion.
        image.CopyPixelDataTo(buffer);

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, palette: null, buffer, stride);

        bitmap.Freeze();
        return bitmap;
    }
}
