using System.IO;

namespace ImageViewer.Imaging;

/// <summary>
/// Tries each decoder tier in turn until one produces an image.
/// </summary>
/// <remarks>
/// <para>
/// Order is cheapest and most likely first: WIC handles the overwhelming majority of real files and
/// is the fastest path to pixels on Windows; ImageSharp rescues the common formats on machines
/// without the optional Windows codecs; Svg.Skia handles vectors; Magick.NET catches everything
/// else at the cost of being by far the heaviest.
/// </para>
/// <para>
/// The format is identified from the file's own bytes first, so tiers that provably cannot handle
/// it are skipped rather than being discovered to fail by exception - and a mislabelled file still
/// reaches a decoder that can read it.
/// </para>
/// </remarks>
public static class DecoderChain
{
    /// <summary>
    /// Decodes <paramref name="bytes"/>, falling through the tiers until one succeeds.
    /// </summary>
    /// <exception cref="InvalidDataException">Every applicable tier declined the file.</exception>
    public static DecodedImage Decode(
        byte[] bytes, string path, int maxWidth, int maxHeight, CancellationToken ct)
    {
        if (bytes.Length == 0)
            throw new InvalidDataException("The file is empty.");

        // Content first, extension only as a tie-breaker for formats no header can identify.
        var kind = FormatSniffer.Identify(bytes, path);
        List<string>? failures = null;

        // Tier 1: WIC. Skipped only for formats Windows has no codec for under any configuration.
        if (FormatSniffer.IsPlausiblyWic(kind))
        {
            try
            {
                return WicDecoder.Decode(bytes, path, maxWidth, maxHeight, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Record(ref failures, WicDecoder.Name, ex);
            }
        }

        // Tier 3 before tier 2 when the content is a vector: ImageSharp cannot parse SVG at all,
        // so there is nothing to gain from trying it first.
        if (SvgDecoder.Supports(kind))
        {
            try
            {
                return SvgDecoder.Decode(bytes, path, maxWidth, maxHeight, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Record(ref failures, SvgDecoder.Name, ex);
            }
        }

        // Tier 2: managed fallback for the everyday formats.
        if (ImageSharpDecoder.Supports(kind) || kind == ImageFormatKind.Unknown)
        {
            try
            {
                return ImageSharpDecoder.Decode(bytes, path, maxWidth, maxHeight, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Record(ref failures, ImageSharpDecoder.Name, ex);
            }
        }

        // Tier 4: the heavyweight, tried last precisely because of what it costs to load.
        try
        {
            return MagickDecoder.Decode(bytes, path, maxWidth, maxHeight, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Record(ref failures, MagickDecoder.Name, ex);
        }

        throw new InvalidDataException(BuildFailureMessage(kind, failures));
    }

    private static void Record(ref List<string>? failures, string tier, Exception ex)
    {
        failures ??= [];
        failures.Add($"{tier}: {ex.Message}");
    }

    /// <summary>
    /// Builds an error naming what the file appeared to be and what each tier said about it.
    /// </summary>
    /// <remarks>
    /// "Could not display" on its own is useless for diagnosis. Reporting the sniffed format plus
    /// each tier's complaint distinguishes a corrupt file from a missing Windows codec.
    /// </remarks>
    private static string BuildFailureMessage(ImageFormatKind kind, List<string>? failures)
    {
        var described = kind == ImageFormatKind.Unknown
            ? "The file does not look like a recognised image format."
            : $"Detected {kind}, but no decoder could read it.";

        if (failures is null or { Count: 0 }) return described;

        return described + Environment.NewLine + string.Join(Environment.NewLine, failures);
    }
}
