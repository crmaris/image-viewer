using System.IO;

namespace ImageViewer.Imaging;

/// <summary>
/// The master list of extensions the viewer will attempt to open.
/// </summary>
/// <remarks>
/// This is deliberately generous. Being listed here only means "worth handing to the decoder
/// chain" - if every tier declines the file, the viewer shows an error rather than crashing. It is
/// better to try and fail on an odd file than to silently skip it during folder navigation.
/// The installer generates its file associations from <see cref="AssociatableExtensions"/>.
/// </remarks>
public static class SupportedFormats
{
    /// <summary>Handled by WIC directly on any Windows install.</summary>
    private static readonly string[] CoreWic =
    [
        ".jpg", ".jpeg", ".jpe", ".jfif", ".jif",
        ".png", ".gif", ".bmp", ".dib",
        ".tif", ".tiff",
        ".ico", ".cur",
        ".wdp", ".jxr", ".hdp",
        ".dds",
    ];

    /// <summary>Handled by WIC only when the matching Windows codec is installed.</summary>
    private static readonly string[] CodecDependent =
    [
        ".heic", ".heif", ".hif",
        ".avif", ".avifs",
        ".webp",
        ".jxl",
    ];

    /// <summary>Camera RAW, via the Microsoft Raw Image Extension or Magick.NET as a fallback.</summary>
    private static readonly string[] Raw =
    [
        ".cr2", ".cr3", ".crw",       // Canon
        ".nef", ".nrw",               // Nikon
        ".arw", ".srf", ".sr2",       // Sony
        ".orf",                       // Olympus
        ".rw2",                       // Panasonic
        ".raf",                       // Fujifilm
        ".pef", ".ptx",               // Pentax
        ".srw",                       // Samsung
        ".dng",                       // Adobe / generic
        ".3fr", ".fff",               // Hasselblad
        ".iiq",                       // Phase One
        ".rwl",                       // Leica
        ".x3f",                       // Sigma
        ".mrw",                       // Minolta
        ".erf",                       // Epson
        ".kdc", ".dcr",               // Kodak
    ];

    /// <summary>Vector formats, rasterised by the Svg.Skia tier.</summary>
    private static readonly string[] Vector = [".svg", ".svgz"];

    /// <summary>Formats that only the Magick.NET catch-all tier is likely to handle.</summary>
    private static readonly string[] Exotic =
    [
        ".psd", ".psb",
        ".xcf",
        ".pcx",
        ".jp2", ".j2k", ".jpf", ".jpx", ".jpm",
        ".exr",
        ".hdr", ".pic",
        ".tga", ".icb", ".vda", ".vst",
        ".qoi",
        ".pbm", ".pgm", ".ppm", ".pnm", ".pam",
        ".xpm", ".xbm",
        ".sgi", ".rgb", ".rgba",
        ".fits", ".fit",
        ".miff",
        ".jbig", ".jbg",
        ".wbmp",
        ".flif",
        ".avs",
        ".cin", ".dpx",
        ".mng",
        ".otb",
        ".palm",
        ".ras", ".sun",
        ".ycbcr",
    ];

    /// <summary>Every extension the viewer will try to open, lower-case, dot-prefixed.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(
            [.. CoreWic, .. CodecDependent, .. Raw, .. Vector, .. Exotic],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extensions the installer offers to associate with the viewer.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="All"/> on purpose: claiming ".pic" or ".rgb" as an image type would
    /// hijack extensions that legitimately belong to other applications. Only formats that are
    /// unambiguously images and that a user would plausibly double-click are offered.
    /// </remarks>
    public static readonly IReadOnlyList<string> AssociatableExtensions =
        [.. CoreWic, .. CodecDependent, .. Raw, .. Vector, ".psd", ".tga", ".jp2", ".exr", ".qoi"];

    /// <summary>True if the extension is one the viewer is willing to attempt.</summary>
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path.AsSpan());
        if (ext.IsEmpty) return false;
        return All.Contains(ext.ToString());
    }
}
