using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>
/// The handful of EXIF fields worth putting in the info overlay.
/// </summary>
/// <remarks>
/// Deliberately a summary rather than a metadata browser: camera, lens and exposure answer "how was
/// this shot taken", which is what someone comparing a folder of test photographs actually wants.
/// Every field is optional, because metadata in the wild is patchy and frequently malformed.
/// </remarks>
public sealed record ExifSummary
{
    public string? Camera { get; init; }
    public string? Lens { get; init; }
    public string? Exposure { get; init; }
    public string? Aperture { get; init; }
    public string? IsoSpeed { get; init; }
    public string? FocalLength { get; init; }
    public string? TakenOn { get; init; }

    public bool HasAnything =>
        Camera is not null || Lens is not null || Exposure is not null || Aperture is not null ||
        IsoSpeed is not null || FocalLength is not null || TakenOn is not null;

    /// <summary>Reads what metadata the file has, returning an empty summary rather than failing.</summary>
    public static ExifSummary Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

            if (decoder.Frames.Count == 0 || decoder.Frames[0].Metadata is not BitmapMetadata meta)
                return new ExifSummary();

            var make = Query<string>(meta, "/app1/ifd/{ushort=271}")?.Trim();
            var model = Query<string>(meta, "/app1/ifd/{ushort=272}")?.Trim();

            return new ExifSummary
            {
                Camera = CombineMakeAndModel(make, model),
                Lens = Query<string>(meta, "/app1/ifd/exif/{ushort=42036}")?.Trim(),
                Exposure = FormatExposure(QueryRational(meta, "/app1/ifd/exif/{ushort=33434}")),
                Aperture = FormatAperture(QueryRational(meta, "/app1/ifd/exif/{ushort=33437}")),
                IsoSpeed = Query<ushort>(meta, "/app1/ifd/exif/{ushort=34855}") is { } iso and > 0
                    ? $"ISO {iso}"
                    : null,
                FocalLength = FormatFocalLength(QueryRational(meta, "/app1/ifd/exif/{ushort=37386}")),
                TakenOn = FormatDate(Query<string>(meta, "/app1/ifd/exif/{ushort=36867}")),
            };
        }
        catch
        {
            return new ExifSummary();
        }
    }

    /// <summary>Most cameras repeat the manufacturer inside the model, so avoid "Canon Canon EOS R5".</summary>
    private static string? CombineMakeAndModel(string? make, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return string.IsNullOrWhiteSpace(make) ? null : make;
        if (string.IsNullOrWhiteSpace(make)) return model;

        return model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
            ? model
            : $"{make} {model}";
    }

    private static string? FormatExposure(double? seconds)
    {
        if (seconds is not { } value || value <= 0) return null;

        // Fast shutter speeds read naturally as a fraction, slow ones as a decimal.
        return value >= 1
            ? $"{value.ToString("0.#", CultureInfo.InvariantCulture)}s"
            : $"1/{Math.Round(1 / value)}s";
    }

    private static string? FormatAperture(double? fNumber) =>
        fNumber is { } value and > 0
            ? $"f/{value.ToString("0.#", CultureInfo.InvariantCulture)}"
            : null;

    private static string? FormatFocalLength(double? mm) =>
        mm is { } value and > 0
            ? $"{value.ToString("0.#", CultureInfo.InvariantCulture)}mm"
            : null;

    /// <summary>EXIF dates use colons in the date part, which no standard parser accepts.</summary>
    private static string? FormatDate(string? exifDate)
    {
        if (string.IsNullOrWhiteSpace(exifDate)) return null;

        return DateTime.TryParseExact(
            exifDate.Trim(), "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : exifDate.Trim();
    }

    private static T? Query<T>(BitmapMetadata meta, string query)
    {
        try { return meta.GetQuery(query) is T value ? value : default; }
        catch { return default; }
    }

    /// <summary>
    /// Reads a rational, which EXIF stores as a packed numerator/denominator pair.
    /// </summary>
    private static double? QueryRational(BitmapMetadata meta, string query)
    {
        try
        {
            switch (meta.GetQuery(query))
            {
                case ulong packed:
                {
                    // Unsigned rational: low 32 bits numerator, high 32 bits denominator.
                    var numerator = (uint)(packed & 0xFFFFFFFF);
                    var denominator = (uint)(packed >> 32);
                    return denominator == 0 ? null : numerator / (double)denominator;
                }
                case long packedSigned:
                {
                    var numerator = (int)(packedSigned & 0xFFFFFFFF);
                    var denominator = (int)(packedSigned >> 32);
                    return denominator == 0 ? null : numerator / (double)denominator;
                }
                case double d:
                    return d;
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }
}
