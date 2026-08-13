using System.IO;

namespace ImageViewer.Imaging;

/// <summary>Image container identified from the file's own bytes.</summary>
public enum ImageFormatKind
{
    Unknown,
    Jpeg, Png, Gif, Bmp, Tiff, Ico, Webp, Heif, Avif, JpegXl, Jxr, Dds,
    Psd, Svg, Targa, Qoi, OpenExr, Jpeg2000, Pnm, Xcf, Pcx, Radiance,
}

/// <summary>
/// Identifies an image format from its leading bytes rather than its file extension.
/// </summary>
/// <remarks>
/// Extensions lie constantly - files saved from a browser, renamed by hand, or exported by a tool
/// that guessed wrong. Sniffing the container means a PNG called <c>photo.jpg</c> still opens, and
/// it also lets the decoder chain skip tiers that provably cannot handle a format instead of
/// discovering that by catching an exception.
/// </remarks>
public static class FormatSniffer
{
    /// <summary>Enough to cover every signature below, including the ISO-BMFF brand at offset 8.</summary>
    public const int HeaderBytes = 32;

    public static ImageFormatKind Identify(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4) return ImageFormatKind.Unknown;

        // JPEG: SOI marker.
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return ImageFormatKind.Jpeg;

        if (header.Length >= 8 && header[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return ImageFormatKind.Png;

        if (header.Length >= 6 &&
            (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
            return ImageFormatKind.Gif;

        if (header[0] == 'B' && header[1] == 'M')
            return ImageFormatKind.Bmp;

        // TIFF: byte-order mark plus magic 42. Most camera RAW formats are TIFF containers, which
        // is fine - the tiers that matter for RAW identify it by extension anyway.
        if (header.Length >= 4 &&
            ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00) ||
             (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A)))
            return ImageFormatKind.Tiff;

        // ICO/CUR. The image count at offset 4 must be non-zero, which is what separates this from
        // an uncompressed TGA: a true-colour TGA also begins 00 00 02 00, but its colour-map length
        // sits at that offset and is zero. Without the count check every .tga is misread as a cursor.
        if (header.Length >= 6 &&
            header[0] == 0x00 && header[1] == 0x00 &&
            (header[2] == 0x01 || header[2] == 0x02) && header[3] == 0x00 &&
            (header[4] | (header[5] << 8)) > 0)
            return ImageFormatKind.Ico;

        // RIFF container: "RIFF" .... "WEBP".
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return ImageFormatKind.Webp;

        // ISO base media: a 4-byte size, then "ftyp", then a brand that names the real format.
        if (header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = header[8..12];
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
                return ImageFormatKind.Avif;
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) ||
                brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("heim"u8) ||
                brand.SequenceEqual("heis"u8) || brand.SequenceEqual("hevm"u8) ||
                brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8))
                return ImageFormatKind.Heif;
        }

        // JPEG XL: raw codestream, or the ISO-BMFF wrapper.
        if (header.Length >= 2 && header[0] == 0xFF && header[1] == 0x0A)
            return ImageFormatKind.JpegXl;
        if (header.Length >= 12 &&
            header[..12].SequenceEqual((ReadOnlySpan<byte>)[0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A]))
            return ImageFormatKind.JpegXl;

        if (header.Length >= 4 && header[..4].SequenceEqual("DDS "u8))
            return ImageFormatKind.Dds;

        if (header.Length >= 4 && header[..4].SequenceEqual("8BPS"u8))
            return ImageFormatKind.Psd;

        if (header.Length >= 3 && header[..3].SequenceEqual((ReadOnlySpan<byte>)[0x49, 0x49, 0xBC]))
            return ImageFormatKind.Jxr;

        if (header.Length >= 4 && header[..4].SequenceEqual("qoif"u8))
            return ImageFormatKind.Qoi;

        if (header.Length >= 4 && header[..4].SequenceEqual((ReadOnlySpan<byte>)[0x76, 0x2F, 0x31, 0x01]))
            return ImageFormatKind.OpenExr;

        if (header.Length >= 12 &&
            header[..12].SequenceEqual((ReadOnlySpan<byte>)[0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A]))
            return ImageFormatKind.Jpeg2000;
        if (header.Length >= 4 && header[..4].SequenceEqual((ReadOnlySpan<byte>)[0xFF, 0x4F, 0xFF, 0x51]))
            return ImageFormatKind.Jpeg2000;

        if (header.Length >= 9 && header[..9].SequenceEqual("gimp xcf "u8))
            return ImageFormatKind.Xcf;

        if (header[0] == 0x0A && header[1] <= 0x05 && header[2] == 0x01)
            return ImageFormatKind.Pcx;

        if (header.Length >= 10 && header[..10].SequenceEqual("#?RADIANCE"u8))
            return ImageFormatKind.Radiance;

        // Netpbm: 'P' followed by a digit 1-7.
        if (header[0] == 'P' && header[1] >= '1' && header[1] <= '7')
            return ImageFormatKind.Pnm;

        // SVG is text, so look for a marker rather than a fixed signature. A BOM or leading
        // whitespace, comments and a DOCTYPE can all precede the root element.
        if (LooksLikeSvg(header)) return ImageFormatKind.Svg;

        // TGA last, and only as a weak guess: the format has no leading magic number at all, just
        // a plausible-looking header, so anything with a real signature must win first.
        if (LooksLikeTarga(header)) return ImageFormatKind.Targa;

        return ImageFormatKind.Unknown;
    }

    /// <summary>
    /// Identifies from a whole buffer, falling back to the extension when the bytes are inconclusive.
    /// </summary>
    /// <remarks>
    /// Content always wins, so a mislabelled file still opens correctly. The extension is consulted
    /// only for formats that genuinely cannot be recognised from their header - a gzipped SVG looks
    /// like any other gzip stream, and camera RAW files are TIFF containers whose real format is
    /// only knowable from the name.
    /// </remarks>
    public static ImageFormatKind Identify(byte[] bytes, string? path = null)
    {
        var kind = Identify(bytes.AsSpan(0, Math.Min(HeaderBytes, bytes.Length)));

        if (kind != ImageFormatKind.Unknown || string.IsNullOrEmpty(path))
            return kind;

        var isGzip = bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".svgz" when isGzip => ImageFormatKind.Svg,
            ".svg" => ImageFormatKind.Svg,
            ".tga" or ".icb" or ".vda" or ".vst" => ImageFormatKind.Targa,
            ".qoi" => ImageFormatKind.Qoi,
            ".pcx" => ImageFormatKind.Pcx,
            _ => ImageFormatKind.Unknown,
        };
    }

    /// <summary>
    /// A plausible uncompressed or RLE TGA header.
    /// </summary>
    /// <remarks>
    /// TGA predates magic numbers, so this validates the fixed fields instead: a known image type,
    /// a colour-map flag that is 0 or 1, and a sane bit depth. Weak by nature, which is why it is
    /// only consulted after every signature-based check has declined.
    /// </remarks>
    private static bool LooksLikeTarga(ReadOnlySpan<byte> header)
    {
        if (header.Length < 18) return false;

        var colourMapType = header[1];
        if (colourMapType > 1) return false;

        // 1/2/3 uncompressed (mapped, true-colour, greyscale), 9/10/11 the RLE equivalents.
        var imageType = header[2];
        if (imageType is not (1 or 2 or 3 or 9 or 10 or 11)) return false;

        var bitsPerPixel = header[16];
        if (bitsPerPixel is not (8 or 15 or 16 or 24 or 32)) return false;

        // A colour-mapped image must have a map, and a true-colour one must not.
        if (colourMapType == 1 && imageType is not (1 or 9)) return false;
        if (colourMapType == 0 && imageType is 1 or 9) return false;

        var width = header[12] | (header[13] << 8);
        var height = header[14] | (header[15] << 8);
        return width > 0 && height > 0;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> header)
    {
        var start = 0;
        if (header.Length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            start = 3;   // UTF-8 BOM

        while (start < header.Length && char.IsWhiteSpace((char)header[start])) start++;
        var rest = header[start..];

        if (rest.Length < 4 || rest[0] != '<') return false;

        return rest.StartsWith("<svg"u8) || rest.StartsWith("<?xml"u8) || rest.StartsWith("<!--"u8) ||
               rest.StartsWith("<!DOCTYPE svg"u8);
    }

    /// <summary>
    /// Whether WIC has any chance with this format, before an attempt is made.
    /// </summary>
    /// <remarks>
    /// Formats listed here still go to WIC first, because the Windows codec - where installed - is
    /// consistently faster than the managed fallbacks. Those it cannot possibly handle skip
    /// straight to the tier that can, avoiding a pointless exception on every single file.
    /// </remarks>
    public static bool IsPlausiblyWic(ImageFormatKind kind) => kind switch
    {
        ImageFormatKind.Svg or ImageFormatKind.Psd or ImageFormatKind.Xcf or
        ImageFormatKind.OpenExr or ImageFormatKind.Pcx or ImageFormatKind.Radiance or
        ImageFormatKind.Pnm or ImageFormatKind.Qoi or ImageFormatKind.Jpeg2000 => false,
        _ => true,
    };
}
