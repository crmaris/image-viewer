using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ImageViewer.Editing;
using ImageViewer.Imaging;
using ImageViewer.Navigation;

namespace ImageViewer.Cli;

/// <summary>
/// The command implementations.
/// </summary>
/// <remarks>
/// Every one of these is a thin shell over machinery the viewer already had: the same tiered
/// decoder, the same content-based format sniffing, the same natural sort, and for rotate and flip
/// the same lossless EXIF writer. The CLI is a second face on the existing engine rather than a
/// parallel implementation that could drift away from what the window shows.
/// </remarks>
internal static class Commands
{
    private const int DefaultJpegQuality = 92;
    private const int DefaultThumbQuality = 85;
    private const int DefaultThumbSize = 256;

    /// <summary>Small enough to be quick, large enough that no decoder refuses it.</summary>
    private const int InfoDecodeBox = 64;

    // ------------------------------------------------------------------- version

    internal static int Version()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Console.WriteLine($"Image Viewer {version?.ToString(3) ?? "unknown"}");
        return CommandLine.Success;
    }

    // ---------------------------------------------------------------------- info

    internal static int Info(Arguments args)
    {
        args.RejectUnknown("json", "quiet");

        var paths = ExpandInputs(args.RequireSome("file"));
        var json = args.Has("json");
        var quiet = args.Has("quiet");
        var failures = 0;

        foreach (var path in paths)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var kind = FormatSniffer.Identify(bytes, path);

                // Decoded into a small box on purpose. DecodedImage always reports the file's own
                // dimensions rather than the decoded ones, so this is exact and stays fast on a
                // 24 MP photograph.
                var image = DecoderChain.Decode(
                    bytes, path, InfoDecodeBox, InfoDecodeBox, CancellationToken.None);

                if (quiet)
                {
                    Console.WriteLine($"{image.PixelWidth}x{image.PixelHeight}");
                    continue;
                }

                var exif = ExifSummary.Read(path);

                if (json) WriteInfoJson(path, kind, image, exif, bytes.LongLength);
                else WriteInfoText(path, kind, image, exif, bytes.LongLength);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{path}: {ex.Message}");
                failures++;
            }
        }

        return failures == 0 ? CommandLine.Success : CommandLine.Failed;
    }

    private static void WriteInfoText(
        string path, ImageFormatKind kind, DecodedImage image, ExifSummary exif, long bytes)
    {
        Console.WriteLine(Path.GetFullPath(path));
        Console.WriteLine($"  format      {kind}");
        Console.WriteLine($"  dimensions  {image.PixelWidth} x {image.PixelHeight}");
        Console.WriteLine($"  size        {FormatBytes(bytes)}");
        Console.WriteLine($"  decoder     {image.DecoderName}");

        if (image.AppliedExifOrientation != 1)
            Console.WriteLine($"  orientation EXIF {image.AppliedExifOrientation}, applied on load");

        var declared = Path.GetExtension(path);
        if (declared.Length > 0 && !MatchesExtension(kind, declared))
            Console.WriteLine($"  NOTE        the extension says {declared} but the bytes say {kind}");

        if (exif.HasAnything)
        {
            foreach (var (label, value) in (( string, string?)[])
                     [
                         ("camera", exif.Camera), ("lens", exif.Lens),
                         ("exposure", exif.Exposure), ("aperture", exif.Aperture),
                         ("iso", exif.IsoSpeed), ("focal", exif.FocalLength),
                         ("taken", exif.TakenOn),
                     ])
            {
                if (!string.IsNullOrWhiteSpace(value))
                    Console.WriteLine($"  {label,-11} {value}");
            }
        }

        Console.WriteLine();
    }

    private static void WriteInfoJson(
        string path, ImageFormatKind kind, DecodedImage image, ExifSummary exif, long bytes)
    {
        using var stream = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("path", Path.GetFullPath(path));
        writer.WriteString("format", kind.ToString());
        writer.WriteNumber("width", image.PixelWidth);
        writer.WriteNumber("height", image.PixelHeight);
        writer.WriteNumber("bytes", bytes);
        writer.WriteString("decoder", image.DecoderName);
        writer.WriteNumber("exifOrientation", image.AppliedExifOrientation);

        WriteIfPresent(writer, "camera", exif.Camera);
        WriteIfPresent(writer, "lens", exif.Lens);
        WriteIfPresent(writer, "exposure", exif.Exposure);
        WriteIfPresent(writer, "aperture", exif.Aperture);
        WriteIfPresent(writer, "iso", exif.IsoSpeed);
        WriteIfPresent(writer, "focalLength", exif.FocalLength);
        WriteIfPresent(writer, "takenOn", exif.TakenOn);

        writer.WriteEndObject();
        writer.Flush();

        // One object per line, so the output pipes straight into jq or a while-read loop.
        stream.Write("\n"u8);
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) writer.WriteString(name, value);
    }

    // ------------------------------------------------------------------ identify

    internal static int Identify(Arguments args)
    {
        args.RejectUnknown("mismatched-only");

        var paths = ExpandInputs(args.RequireSome("file"));
        var onlyMismatched = args.Has("mismatched-only");
        var failures = 0;

        foreach (var path in paths)
        {
            try
            {
                // Header only: identification never needs the pixels, which is what makes this
                // usable across a library of thousands of files.
                var header = ReadHeader(path);
                var kind = FormatSniffer.Identify(header, path);
                var declared = Path.GetExtension(path);
                var mismatched = declared.Length > 0 && !MatchesExtension(kind, declared);

                if (onlyMismatched && !mismatched) continue;

                Console.WriteLine(mismatched
                    ? $"{path}: {kind}  (extension says {declared})"
                    : $"{path}: {kind}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{path}: {ex.Message}");
                failures++;
            }
        }

        return failures == 0 ? CommandLine.Success : CommandLine.Failed;
    }

    private static byte[] ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);

        // Enough for the sniffer, and enough for the extension fallback to have something to work
        // with on formats that have no magic number at all.
        var buffer = new byte[Math.Max(FormatSniffer.HeaderBytes, 64)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        return read == buffer.Length ? buffer : buffer[..read];
    }

    // ---------------------------------------------------------------------- list

    internal static int List(Arguments args)
    {
        args.RejectUnknown("names", "count", "absolute");

        var folder = args.Positional.Count == 0 ? "." : args.RequireOne("folder");

        if (!Directory.Exists(folder))
            throw new UsageException($"'{folder}' is not a folder");

        // The viewer's own scanner, so the order printed is exactly the order Space walks through.
        var files = FolderScanner.ScanAsync(folder, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (args.Has("count"))
        {
            Console.WriteLine(files.Length);
            return CommandLine.Success;
        }

        foreach (var file in files)
            Console.WriteLine(args.Has("names") ? Path.GetFileName(file) : file);

        return CommandLine.Success;
    }

    // ------------------------------------------------------------------- formats

    internal static int Formats(Arguments args)
    {
        args.RejectUnknown("readable", "writable", "bare");

        var wantReadable = !args.Has("writable");
        var wantWritable = !args.Has("readable");

        if (args.Has("bare"))
        {
            var bare = wantReadable ? SupportedFormats.All.Order() : WritableExtensions().Order();
            foreach (var extension in bare) Console.WriteLine(extension);
            return CommandLine.Success;
        }

        if (wantReadable)
        {
            Console.WriteLine("Readable");
            Console.WriteLine($"  {SupportedFormats.All.Count} extensions, decoded by the first tier that accepts the file:");
            Console.WriteLine("    1  WIC          JPEG PNG GIF BMP TIFF ICO DDS JXR, plus HEIC/WebP/JPEG-XL/AVIF/RAW");
            Console.WriteLine("                    wherever the matching Windows codec is installed");
            Console.WriteLine("    2  ImageSharp   JPEG PNG GIF BMP TIFF WebP TGA PNM, with no Windows codecs at all");
            Console.WriteLine("    3  Svg.Skia     SVG SVGZ");
            Console.WriteLine("    4  Magick.NET   PSD XCF EXR JP2 PCX HDR QOI and several hundred more");
            Console.WriteLine();
            Console.WriteLine("  " + Wrap(SupportedFormats.All.Order(), 74, "  "));
            Console.WriteLine();
        }

        if (wantWritable)
        {
            Console.WriteLine("Writable");
            Console.WriteLine("  " + Wrap(WritableExtensions().Order(), 74, "  "));
            Console.WriteLine();
            Console.WriteLine("  Other targets are handed to Magick.NET, which writes most of what it reads.");
        }

        return CommandLine.Success;
    }

    private static IEnumerable<string> WritableExtensions() =>
        [".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".tif", ".tiff", ".gif", ".wdp", ".jxr", ".hdp"];

    // ------------------------------------------------------- convert / resize / thumb

    internal static int Convert(Arguments args)
    {
        args.RejectUnknown("quality", "out-dir", "format", "overwrite");

        return Transform(args, new TransformOptions
        {
            Quality = args.Integer("quality") ?? DefaultJpegQuality,
            MaxWidth = 0,
            MaxHeight = 0,
        });
    }

    internal static int Resize(Arguments args)
    {
        args.RejectUnknown("width", "height", "quality", "out-dir", "format", "overwrite", "allow-upscale");

        var width = args.Integer("width") ?? 0;
        var height = args.Integer("height") ?? 0;

        if (width == 0 && height == 0)
            throw new UsageException("resize needs --width, --height, or both");

        return Transform(args, new TransformOptions
        {
            Quality = args.Integer("quality") ?? DefaultJpegQuality,
            // A single axis means "constrain that one only", which is an enormous bound on the
            // other rather than a second constraint that would crop the aspect ratio.
            MaxWidth = width == 0 ? int.MaxValue : width,
            MaxHeight = height == 0 ? int.MaxValue : height,
            AllowUpscale = args.Has("allow-upscale"),
        });
    }

    internal static int Thumb(Arguments args)
    {
        args.RejectUnknown("size", "quality", "out-dir", "format", "overwrite", "embedded");

        var size = args.Integer("size") ?? DefaultThumbSize;

        return Transform(args, new TransformOptions
        {
            Quality = args.Integer("quality") ?? DefaultThumbQuality,
            MaxWidth = size,
            MaxHeight = size,
            PreferEmbedded = args.Has("embedded"),
            DefaultExtension = ".jpg",
        });
    }

    private sealed class TransformOptions
    {
        internal int Quality { get; init; }
        internal int MaxWidth { get; init; }
        internal int MaxHeight { get; init; }
        internal bool AllowUpscale { get; init; }
        internal bool PreferEmbedded { get; init; }
        internal string? DefaultExtension { get; init; }
    }

    /// <summary>
    /// The shared body of convert, resize and thumb.
    /// </summary>
    /// <remarks>
    /// All three are the same operation with different defaults: decode, optionally to a bounded
    /// size, then encode somewhere else. Keeping them one implementation is what stops
    /// <c>resize</c> and <c>thumb</c> quietly disagreeing about what "fits in a box" means.
    /// </remarks>
    private static int Transform(Arguments args, TransformOptions options)
    {
        var outDir = args.Value("out-dir");
        var format = NormaliseExtension(args.Value("format")) ?? options.DefaultExtension;
        var overwrite = args.Has("overwrite");

        List<(string Input, string Output)> jobs = [];

        if (outDir is not null)
        {
            var inputs = ExpandInputs(args.RequireSome("input file"));
            Directory.CreateDirectory(outDir);

            foreach (var input in inputs)
            {
                var extension = format ?? Path.GetExtension(input);
                var name = Path.GetFileNameWithoutExtension(input) + extension;
                jobs.Add((input, Path.Combine(outDir, name)));
            }
        }
        else
        {
            var positional = args.Positional;

            if (positional.Count != 2)
            {
                throw new UsageException(positional.Count < 2
                    ? "needs an input and an output file, or --out-dir for several inputs"
                    : "several inputs need --out-dir to say where the results go");
            }

            jobs.Add((positional[0], positional[1]));
        }

        var failures = 0;

        foreach (var (input, output) in jobs)
        {
            try
            {
                if (!overwrite && File.Exists(output) &&
                    !string.Equals(Path.GetFullPath(input), Path.GetFullPath(output),
                        StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"{output}: already exists (use --overwrite)");
                    failures++;
                    continue;
                }

                var image = LoadForTransform(input, options);
                ImageWriter.Write(image, output, options.Quality);

                Console.WriteLine($"{input} -> {output}  ({image.PixelWidth}x{image.PixelHeight})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{input}: {ex.Message}");
                failures++;
            }
        }

        return failures == 0 ? CommandLine.Success : CommandLine.Failed;
    }

    private static BitmapSource LoadForTransform(string path, TransformOptions options)
    {
        var bytes = File.ReadAllBytes(path);
        var width = options.MaxWidth;
        var height = options.MaxHeight;

        if (options.PreferEmbedded)
        {
            var preview = WicDecoder.TryDecodeEmbeddedThumbnail(bytes, path, CancellationToken.None);

            // The preview is whatever size the camera chose to store, which can be larger than the
            // box that was asked for. Fitting it afterwards costs almost nothing and keeps
            // --embedded an optimisation rather than a different answer to the same question.
            if (preview is not null) return Rescale(preview.Bitmap, width, height, enlarge: false);
        }

        var decoded = DecoderChain.Decode(bytes, path, width, height, CancellationToken.None);

        // Decode-to-fit never enlarges, which is the right default - upscaling by accident produces
        // a bigger file that holds no more detail. --allow-upscale is the only way to ask for it.
        return options.AllowUpscale
            ? Rescale(decoded.Bitmap, width, height, enlarge: true)
            : decoded.Bitmap;
    }

    /// <summary>
    /// Scales an image to fit a bounding box, preserving its aspect ratio.
    /// </summary>
    /// <param name="enlarge">
    /// When true, scales up an image smaller than the box; when false, only ever scales down.
    /// </param>
    /// <remarks>
    /// <see cref="int.MaxValue"/> on an axis means "unconstrained", which is how resize expresses
    /// a single-axis limit. Treating it as a real bound would compute a scale factor of about ten
    /// million and try to allocate the result.
    /// </remarks>
    private static BitmapSource Rescale(BitmapSource image, int maxWidth, int maxHeight, bool enlarge)
    {
        if (maxWidth <= 0 || maxHeight <= 0) return image;
        if (maxWidth == int.MaxValue && maxHeight == int.MaxValue) return image;

        var horizontal = maxWidth == int.MaxValue ? double.MaxValue : maxWidth / (double)image.PixelWidth;
        var vertical = maxHeight == int.MaxValue ? double.MaxValue : maxHeight / (double)image.PixelHeight;

        var scale = Math.Min(horizontal, vertical);
        if (double.IsInfinity(scale) || scale <= 0) return image;

        // Nothing to do when the image is already on the right side of the bound.
        if (enlarge ? scale <= 1 : scale >= 1) return image;

        var transform = new System.Windows.Media.ScaleTransform(scale, scale);
        transform.Freeze();

        var scaled = new TransformedBitmap(image, transform);
        scaled.Freeze();

        return scaled;
    }

    // ------------------------------------------------------------- rotate / flip

    internal static int Rotate(Arguments args)
    {
        args.RejectUnknown("cw", "ccw", "180", "re-encode");

        var turns = new[] { args.Has("cw"), args.Has("ccw"), args.Has("180") }.Count(x => x);
        if (turns == 0) throw new UsageException("rotate needs --cw, --ccw or --180");
        if (turns > 1) throw new UsageException("give only one of --cw, --ccw and --180");

        var degrees = args.Has("cw") ? 90 : args.Has("ccw") ? 270 : 180;

        return ApplyEdit(args, flipHorizontal: false, flipVertical: false, degrees);
    }

    internal static int Flip(Arguments args)
    {
        args.RejectUnknown("horizontal", "vertical", "re-encode");

        var horizontal = args.Has("horizontal");
        var vertical = args.Has("vertical");

        if (!horizontal && !vertical)
            throw new UsageException("flip needs --horizontal or --vertical");

        return ApplyEdit(args, horizontal, vertical, rotation: 0);
    }

    private static int ApplyEdit(Arguments args, bool flipHorizontal, bool flipVertical, int rotation)
    {
        var paths = ExpandInputs(args.RequireSome("file"));
        var reEncode = args.Has("re-encode");
        var failures = 0;

        foreach (var path in paths)
        {
            try
            {
                // The viewer's own writer, so a JPEG rotated here is byte-for-byte as lossless as
                // one rotated with Ctrl+S in the window.
                var result = ImageSaver.Save(
                    path, flipHorizontal, flipVertical, rotation, CancellationToken.None, reEncode);

                Console.WriteLine($"{path}: {result.Description}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{path}: {ex.Message}");
                failures++;
            }
        }

        return failures == 0 ? CommandLine.Success : CommandLine.Failed;
    }

    // ------------------------------------------------------------------- helpers

    /// <summary>
    /// Turns the given inputs into a concrete file list.
    /// </summary>
    /// <remarks>
    /// A folder expands to the images inside it, in viewing order, so <c>info shots</c> works the
    /// way anyone would expect. Wildcards are expanded here too: cmd.exe does not do it for the
    /// process the way a Unix shell would, so without this <c>*.jpg</c> would arrive as a literal
    /// string and every command would report a file that does not exist.
    /// </remarks>
    private static IReadOnlyList<string> ExpandInputs(IReadOnlyList<string> inputs)
    {
        List<string> resolved = [];

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                resolved.AddRange(
                    FolderScanner.ScanAsync(input, CancellationToken.None).GetAwaiter().GetResult());
                continue;
            }

            if (input.Contains('*') || input.Contains('?'))
            {
                var folder = Path.GetDirectoryName(input);
                if (string.IsNullOrEmpty(folder)) folder = ".";

                var pattern = Path.GetFileName(input);

                if (Directory.Exists(folder))
                {
                    var matches = Directory.GetFiles(folder, pattern);
                    Array.Sort(matches, FolderScanner.CompareNatural);
                    resolved.AddRange(matches);
                }

                continue;
            }

            resolved.Add(input);
        }

        if (resolved.Count == 0) throw new UsageException("no files matched");

        return resolved;
    }

    /// <summary>True if the sniffed format is consistent with the file's extension.</summary>
    private static bool MatchesExtension(ImageFormatKind kind, string extension)
    {
        var normalised = extension.TrimStart('.').ToLowerInvariant();

        return kind switch
        {
            ImageFormatKind.Jpeg => normalised is "jpg" or "jpeg" or "jpe" or "jfif" or "jif",
            ImageFormatKind.Png => normalised is "png",
            ImageFormatKind.Gif => normalised is "gif",
            ImageFormatKind.Bmp => normalised is "bmp" or "dib",
            ImageFormatKind.Tiff => normalised is "tif" or "tiff" or "dng" or "nef" or "cr2"
                                                or "arw" or "orf" or "rw2" or "pef" or "srw"
                                                or "nrw" or "sr2" or "srf" or "raf" or "3fr"
                                                or "fff" or "iiq" or "rwl" or "erf" or "kdc"
                                                or "dcr" or "mrw" or "ptx",
            ImageFormatKind.Ico => normalised is "ico" or "cur",
            ImageFormatKind.Webp => normalised is "webp",
            ImageFormatKind.Svg => normalised is "svg" or "svgz",
            ImageFormatKind.Psd => normalised is "psd" or "psb",
            ImageFormatKind.Targa => normalised is "tga" or "icb" or "vda" or "vst",
            ImageFormatKind.Heif => normalised is "heic" or "heif" or "hif",
            ImageFormatKind.Avif => normalised is "avif" or "avifs" or "heic" or "heif",
            ImageFormatKind.JpegXl => normalised is "jxl",
            ImageFormatKind.Jxr => normalised is "jxr" or "wdp" or "hdp",
            ImageFormatKind.Dds => normalised is "dds",
            ImageFormatKind.Qoi => normalised is "qoi",
            ImageFormatKind.OpenExr => normalised is "exr",
            ImageFormatKind.Jpeg2000 => normalised is "jp2" or "j2k" or "jpf" or "jpx" or "jpm",
            ImageFormatKind.Pnm => normalised is "pbm" or "pgm" or "ppm" or "pnm" or "pam",
            ImageFormatKind.Xcf => normalised is "xcf",
            ImageFormatKind.Pcx => normalised is "pcx",
            ImageFormatKind.Radiance => normalised is "hdr" or "pic",
            // Anything the sniffer could not place is not evidence of a mismatch.
            ImageFormatKind.Unknown => true,
            _ => true,
        };
    }

    private static string? NormaliseExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        return extension.StartsWith('.') ? extension : "." + extension;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    /// <summary>Wraps a list of short strings to a column width.</summary>
    private static string Wrap(IEnumerable<string> items, int width, string indent)
    {
        var builder = new System.Text.StringBuilder();
        var column = 0;

        foreach (var item in items)
        {
            if (column > 0 && column + item.Length + 1 > width)
            {
                builder.Append(Environment.NewLine).Append(indent);
                column = 0;
            }

            builder.Append(item).Append(' ');
            column += item.Length + 1;
        }

        return builder.ToString().TrimEnd();
    }
}
