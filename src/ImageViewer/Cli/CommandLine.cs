using System.IO;

namespace ImageViewer.Cli;

/// <summary>
/// The command-line interface: argument parsing, dispatch and help.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is touched when the application is launched normally to view an image.
/// <see cref="IsCommand"/> is a handful of string comparisons on the startup path, and the CLR does
/// not JIT - or load anything referenced by - the rest of this class unless a command actually
/// runs. That matters: the CLI reaches for the encoder and the fallback decoders, which are exactly
/// the assemblies the viewer works so hard to keep unloaded.
/// </para>
/// </remarks>
internal static class CommandLine
{
    internal const int Success = 0;
    internal const int Failed = 1;
    internal const int UsageError = 2;

    /// <summary>Verbs that switch the process from "show a window" to "print and exit".</summary>
    private static readonly string[] Verbs =
    [
        "info", "identify", "list", "formats", "convert", "resize",
        "thumb", "rotate", "flip", "version", "help",
    ];

    /// <summary>
    /// True if these arguments ask for a command rather than an image to view.
    /// </summary>
    /// <remarks>
    /// A file genuinely named <c>info</c> would be ambiguous, so <c>--</c> forces everything after
    /// it to be treated as a path: <c>ImageViewer -- info</c> opens the file. The reverse mistake -
    /// a stray verb-shaped filename silently printing help instead of opening - is the one worth
    /// having an escape hatch for.
    /// </remarks>
    internal static bool IsCommand(string[] args)
    {
        if (args.Length == 0) return false;

        var first = args[0];

        if (first == "--") return false;

        if (first is "--help" or "-h" or "-?" or "/?" or "--version") return true;

        if (Verbs.Contains(first, StringComparer.OrdinalIgnoreCase)) return true;

        return LooksLikeMistypedVerb(first);
    }

    /// <summary>
    /// True for a bare word that is neither a known verb nor anything on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, <c>imageviewer conver a.jpg b.png</c> takes the typo for a file name and opens
    /// a window reporting that "conver" could not be found, which is the least useful response
    /// available - and from a script, no response at all. A word with no extension, no directory
    /// separator and no file behind it is a mistyped command far more often than it is an image.
    /// </para>
    /// <para>
    /// The cheap tests come first deliberately. A real launch passes a full path, which contains a
    /// colon on this platform and returns before either <see cref="File.Exists"/> call, so opening
    /// an image never pays for a disk probe it does not need.
    /// </para>
    /// </remarks>
    private static bool LooksLikeMistypedVerb(string argument)
    {
        if (argument.Length == 0 || argument.StartsWith('-')) return false;

        if (argument.Contains(Path.DirectorySeparatorChar) ||
            argument.Contains(Path.AltDirectorySeparatorChar) ||
            argument.Contains(':'))
        {
            return false;
        }

        if (Path.HasExtension(argument)) return false;

        return !File.Exists(argument) && !Directory.Exists(argument);
    }

    /// <summary>Runs a command and returns the process exit code.</summary>
    internal static int Run(string[] args)
    {
        ConsoleHost.Prepare();

        try
        {
            return Dispatch(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"imageviewer: {ex.Message}");
            Console.Error.WriteLine("Try 'imageviewer help' for usage.");
            return UsageError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"imageviewer: {ex.Message}");
            return Failed;
        }
        finally
        {
            ConsoleHost.Finish();
        }
    }

    private static int Dispatch(string[] args)
    {
        var verb = args[0].ToLowerInvariant();

        if (verb is "--help" or "-h" or "-?" or "/?") verb = "help";
        if (verb is "--version") verb = "version";

        var rest = new Arguments(args.Skip(1));

        return verb switch
        {
            "help" => Help(rest),
            "version" => Commands.Version(),
            "info" => Commands.Info(rest),
            "identify" => Commands.Identify(rest),
            "list" => Commands.List(rest),
            "formats" => Commands.Formats(rest),
            "convert" => Commands.Convert(rest),
            "resize" => Commands.Resize(rest),
            "thumb" => Commands.Thumb(rest),
            "rotate" => Commands.Rotate(rest),
            "flip" => Commands.Flip(rest),
            _ => throw new UsageException($"unknown command '{args[0]}'"),
        };
    }

    private static int Help(Arguments args)
    {
        var topic = args.Positional.FirstOrDefault()?.ToLowerInvariant();

        if (topic is not null && Verbs.Contains(topic, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(TopicHelp(topic));
            return Success;
        }

        Console.WriteLine(GeneralHelp);
        return Success;
    }

    internal const string GeneralHelp = """
        Image Viewer - a fast image viewer with a command-line interface.

        VIEWING
          imageviewer <file|folder>          open in the viewer
          imageviewer <file> --fullscreen    open full screen
          imageviewer <folder> --slideshow[=SECONDS]
                                             open and start a slideshow
          imageviewer -- <path>              open a path whose name looks like a command

        COMMANDS
          info <file>...                     dimensions, format, decoder and EXIF
          identify <file>...                 detect the real format from the file's bytes
          list <folder>                      list images in viewing order
          formats                            show every readable and writable format
          convert <in> <out>                 convert between formats
          resize <in> <out> --width N        resize, preserving aspect ratio
          thumb <in> <out> --size N          write a thumbnail
          rotate <file> --cw|--ccw|--180     rotate in place, losslessly for JPEG
          flip <file> --horizontal|--vertical
                                             mirror in place, losslessly for JPEG
          version                            print the version
          help [command]                     show this, or detail for one command

        Run 'imageviewer help <command>' for the options each one takes.

        EXIT CODES
          0  success        1  something failed        2  bad usage
        """;

    private static string TopicHelp(string topic) => topic switch
    {
        "info" => """
            imageviewer info <file>...

            Prints what the viewer knows about each file: the format identified from its bytes,
            the pixel dimensions as stored, which decoder tier handled it, the EXIF orientation,
            and a camera summary when there is one.

              --json        emit one JSON object per file
              --quiet       dimensions only, as WIDTHxHEIGHT

            Decoding is done at a small target size, so this stays fast on large photographs; the
            dimensions reported are always the file's own, never the downscaled ones.
            """,

        "identify" => """
            imageviewer identify <file>...

            Reads only the file header and reports the format its bytes actually are, which is not
            always what the extension claims. Flags any mismatch. Does not decode, so it is fast
            enough to run over a whole library.

              --mismatched-only   list only files whose extension disagrees with their content
            """,

        "list" => """
            imageviewer list <folder>

            Lists the images in the folder in the same order the viewer pages through them, which
            is natural sort - img2 before img10, not after it.

              --names       file names only, without the folder
              --count       print just the number of images
              --absolute    full paths (the default when a folder is given)
            """,

        "formats" => """
            imageviewer formats

            Lists every extension the viewer will attempt, grouped by the decoder tier that
            handles it, and separately the formats it can write.

              --readable    only the formats that can be read
              --writable    only the formats that can be written
              --bare        one extension per line, for scripting
            """,

        "convert" => """
            imageviewer convert <in> <out>
            imageviewer convert <in>... --out-dir <dir> --format <ext>

            Converts between formats. The output format comes from the target extension.

              --quality N   JPEG quality, 1-100 (default 92)
              --out-dir D   write into D, keeping each input's base name
              --format EXT  target extension when using --out-dir
              --overwrite   replace an existing output file

            Transparency is composited onto white when the target cannot carry alpha.
            """,

        "resize" => """
            imageviewer resize <in> <out> --width N [--height N]
            imageviewer resize <in>... --out-dir <dir> --width N

            Resizes to fit within the given bounds, never distorting and never enlarging.
            Give one of --width or --height to constrain that axis alone.

              --width N     maximum width in pixels
              --height N    maximum height in pixels
              --quality N   JPEG quality, 1-100 (default 92)
              --out-dir D   write into D, keeping each input's base name
              --format EXT  target extension when using --out-dir
              --allow-upscale
                            permit output larger than the source
              --overwrite   replace an existing output file

            The resize happens during decoding, so a 24 MP photograph is never fully decoded just
            to be thrown away.
            """,

        "thumb" => """
            imageviewer thumb <in> <out> --size N
            imageviewer thumb <in>... --out-dir <dir> --size N

            Writes a thumbnail fitting inside an N-pixel box (default 256).

              --size N      bounding box in pixels (default 256)
              --quality N   JPEG quality, 1-100 (default 85)
              --out-dir D   write into D, keeping each input's base name
              --format EXT  target extension when using --out-dir (default .jpg)
              --embedded    use the file's own embedded preview when it has one, which is
                            roughly thirty times faster but limited to its stored size
              --overwrite   replace an existing output file
            """,

        "rotate" => """
            imageviewer rotate <file>... --cw|--ccw|--180

            Rotates in place. For JPEG this rewrites only the EXIF orientation tag, leaving the
            compressed image data untouched, so it is genuinely lossless however many times it is
            repeated. PNG, BMP and TIFF are re-encoded, which is lossless for those formats.

              --cw          quarter turn clockwise
              --ccw         quarter turn anticlockwise
              --180         half turn
              --re-encode   physically rotate the pixels instead of setting the tag. Lossy for
                            JPEG; only needed for software that ignores EXIF orientation.
            """,

        "flip" => """
            imageviewer flip <file>... --horizontal|--vertical

            Mirrors in place, losslessly for JPEG by the same EXIF mechanism as rotate.

              --horizontal  mirror left to right
              --vertical    mirror top to bottom
              --re-encode   physically mirror the pixels instead of setting the tag
            """,

        _ => GeneralHelp,
    };
}

/// <summary>Signals a mistake in how the command was invoked, rather than a failure running it.</summary>
internal sealed class UsageException(string message) : Exception(message);
