using System.Diagnostics;
using System.IO;
using ImageViewer;
using ImageViewer.Cli;

namespace ImageViewer.SelfTest;

/// <summary>
/// Checks over the command-line interface: what counts as a command, how arguments parse, and
/// whether the built executable actually behaves when driven from a shell.
/// </summary>
internal static class CliChecks
{
    public static void Run(FeatureChecks.CheckFn check, Action<string> section)
    {
        RunDispatchChecks(check, section);
        RunArgumentChecks(check, section);
        RunLaunchOptionChecks(check, section);
        RunPathInstallChecks(check, section);
        RunEndToEndChecks(check, section);
    }

    // -------------------------------------------------------------- PATH integration

    /// <summary>
    /// Checks the installer's PATH handling, which cannot be run locally.
    /// </summary>
    /// <remarks>
    /// Inno Setup is not installed on this machine, so the only local guard on these lines is
    /// reading them. The one that genuinely matters is the absence of <c>uninsdeletevalue</c>: on a
    /// PATH entry that flag does not remove the folder that was added, it deletes the entire PATH
    /// value on uninstall. That is unrecoverable for the user and completely silent until the next
    /// time they open a shell.
    /// </remarks>
    private static void RunPathInstallChecks(FeatureChecks.CheckFn check, Action<string> section)
    {
        section("CLI: PATH integration in the installer");

        var iss = FindRepoFile("packaging", "ImageViewer.iss");

        if (iss is null)
        {
            check("the installer script could be found", false, "packaging/ImageViewer.iss");
            return;
        }

        var text = File.ReadAllText(iss);

        check("the installer offers a task to put the CLI on PATH",
            text.Contains("addtopath", StringComparison.Ordinal));

        var pathLines = text
            .Split('\n')
            .Where(line => line.Contains("ValueName: \"Path\"", StringComparison.Ordinal))
            .ToArray();

        check("PATH is written for both an all-users and a per-user install",
            pathLines.Length == 2, $"found {pathLines.Length} PATH lines");

        // The catastrophic one. uninsdeletevalue on a PATH entry deletes the whole variable.
        check("no PATH line carries uninsdeletevalue, which would wipe the whole variable",
            pathLines.All(line => !line.Contains("uninsdeletevalue", StringComparison.OrdinalIgnoreCase)),
            "uninstalling would delete the user's entire PATH");

        // {olddata} keeps the existing value unexpanded; reading and rewriting it would turn
        // %SystemRoot% into a literal path and quietly change what the environment means.
        check("PATH is extended with {olddata} rather than read and rewritten",
            pathLines.All(line => line.Contains("{olddata}", StringComparison.Ordinal)));

        check("PATH keeps its REG_EXPAND_SZ type",
            pathLines.All(line => line.Contains("preservestringtype", StringComparison.Ordinal)));

        check("the environment change is broadcast so a new console sees it",
            System.Text.RegularExpressions.Regex.IsMatch(
                text, @"^\s*ChangesEnvironment\s*=\s*yes",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Multiline));

        check("uninstall takes the folder back out of PATH",
            text.Contains("RemoveFromPath()", StringComparison.Ordinal));
    }

    /// <summary>Walks up from the test binary to find a file in the repository.</summary>
    private static string? FindRepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. relativeParts]);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    // ------------------------------------------------------------- what is a command

    private static void RunDispatchChecks(FeatureChecks.CheckFn check, Action<string> section)
    {
        section("CLI: telling a command from a file");

        foreach (var verb in (string[])["info", "convert", "RESIZE", "help", "--version", "-h"])
        {
            check($"'{verb}' is treated as a command",
                CommandLine.IsCommand([verb]));
        }

        // The whole point of the design: a real launch must never be mistaken for a command, or
        // double-clicking an image would print help into a console nobody is looking at.
        foreach (var path in (string[])
                 [@"C:\photos\holiday.jpg", @"D:\a folder\image.png", "picture.jpeg", @"..\up.gif"])
        {
            check($"'{path}' is treated as a path", !CommandLine.IsCommand([path]));
        }

        check("no arguments at all is not a command", !CommandLine.IsCommand([]));

        // "--" is the escape hatch for a file that happens to be named like a verb.
        check("a leading -- forces the rest to be paths",
            !CommandLine.IsCommand(["--", "info"]));

        // A bare word that is not a verb and is not on disk is a typo, not a file. Opening a window
        // to report that "conver" does not exist would be the least useful possible response.
        check("a mistyped verb is caught rather than opened as a file",
            CommandLine.IsCommand(["conver", "a.jpg", "b.png"]));

        // ...but a bare word that IS on disk is a real path and must still open.
        var scratch = Path.Combine(Path.GetTempPath(), "imageviewer-clitest");
        Directory.CreateDirectory(scratch);
        var extensionless = Path.Combine(scratch, "holiday");
        File.WriteAllText(extensionless, "not really an image, but it exists");

        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(scratch);
            check("an extensionless file that exists is still opened, not rejected",
                !CommandLine.IsCommand(["holiday"]));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    // --------------------------------------------------------------- argument parsing

    private static void RunArgumentChecks(FeatureChecks.CheckFn check, Action<string> section)
    {
        section("CLI: argument parsing");

        var separated = new Arguments(["in.jpg", "out.png", "--width", "800", "--overwrite"]);
        check("--flag value binds the value to the flag",
            separated.Integer("width") == 800, $"got {separated.Integer("width")}");
        check("a value-taking flag does not leave its value among the files",
            separated.Positional.SequenceEqual((string[])["in.jpg", "out.png"]),
            $"positional: [{string.Join(", ", separated.Positional)}]");
        check("a flag with no value is still recognised", separated.Has("overwrite"));

        var joined = new Arguments(["--width=640", "--quality=80", "a.jpg"]);
        check("--flag=value parses the same way",
            joined.Integer("width") == 640 && joined.Integer("quality") == 80);

        // The failure this exists to prevent: a boolean flag swallowing the file after it.
        var boolean = new Arguments(["--embedded", "photo.jpg"]);
        check("a boolean flag does not swallow the file after it",
            boolean.Positional.SequenceEqual((string[])["photo.jpg"]),
            $"positional: [{string.Join(", ", boolean.Positional)}]");

        var literal = new Arguments(["--", "--strange-name.jpg"]);
        check("-- lets a file whose name starts with a dash be addressed",
            literal.Positional.SequenceEqual((string[])["--strange-name.jpg"]));

        // A silently ignored typo would write a whole batch at the wrong setting with no hint.
        var typo = new Arguments(["--qualty", "50", "a.jpg"]);
        var rejected = Throws<UsageException>(() => typo.RejectUnknown("quality"));
        check("an unknown option is rejected rather than ignored", rejected);

        var bad = new Arguments(["--width", "wide"]);
        check("a non-numeric value is reported, not silently dropped",
            Throws<UsageException>(() => bad.Integer("width")));

        var negative = new Arguments(["--width=-5"]);
        check("a non-positive size is rejected",
            Throws<UsageException>(() => negative.Integer("width")));

        var empty = new Arguments([]);
        check("an absent flag reads as null rather than throwing", empty.Integer("width") is null);
        check("a missing required file is reported",
            Throws<UsageException>(() => empty.RequireOne("file")));
    }

    // ----------------------------------------------------------------- launch options

    private static void RunLaunchOptionChecks(FeatureChecks.CheckFn check, Action<string> section)
    {
        section("CLI: launch options");

        var plain = LaunchOptions.Parse([@"C:\photos\a.jpg"]);
        check("a plain path is the image to open",
            plain.Paths.SequenceEqual((string[])[@"C:\photos\a.jpg"]) &&
            !plain.Fullscreen && !plain.Slideshow);

        var full = LaunchOptions.Parse([@"C:\photos", "--fullscreen", "--slideshow=6"]);
        check("--fullscreen and --slideshow=N are understood",
            full.Fullscreen && full.Slideshow && full.SlideshowSeconds == 6,
            $"fullscreen={full.Fullscreen} slideshow={full.Slideshow} seconds={full.SlideshowSeconds}");
        check("switches are not mistaken for paths",
            full.Paths.SequenceEqual((string[])[@"C:\photos"]),
            $"paths: [{string.Join(", ", full.Paths)}]");

        // InvariantGlobalization is off in this project, so a decimal point has to keep working on
        // a machine whose locale uses a comma - a shortcut or script would have been written with one.
        var fractional = LaunchOptions.Parse(["x.jpg", "--slideshow=2.5"]);
        check("a fractional interval parses regardless of locale",
            fractional.SlideshowSeconds == 2.5, $"got {fractional.SlideshowSeconds}");

        // Third-party launchers pass switches of their own; refusing to open over one would be a
        // poor trade for an image viewer.
        var unknown = LaunchOptions.Parse(["--some-other-launchers-switch", "photo.jpg"]);
        check("an unrecognised switch is ignored rather than fatal",
            unknown.Paths.SequenceEqual((string[])["photo.jpg"]));

        var escaped = LaunchOptions.Parse(["--", "info"]);
        check("-- opens a file named like a command",
            escaped.Paths.SequenceEqual((string[])["info"]));
    }

    // ---------------------------------------------------------------------- end to end

    /// <summary>
    /// Drives the real executable, if this checkout has one built.
    /// </summary>
    /// <remarks>
    /// Skipped rather than failed when the application has not been built, or has been built in a
    /// different configuration from the test. A check that goes red because of how someone invoked
    /// the build teaches nobody anything, and CI builds the two projects separately.
    /// </remarks>
    private static void RunEndToEndChecks(FeatureChecks.CheckFn check, Action<string> section)
    {
        section("CLI: driving the real executable");

        var exe = FindViewerExecutable();

        if (exe is null)
        {
            Console.WriteLine("    note: ImageViewer.exe not built here, end-to-end CLI checks skipped");
            return;
        }

        var (code, output, _) = RunCommand(exe, ["version"]);
        check("'version' exits cleanly and prints a version",
            code == 0 && output.Contains("Image Viewer", StringComparison.Ordinal),
            $"exit {code}, output '{output.Trim()}'");

        var (helpCode, helpOutput, _) = RunCommand(exe, ["help"]);
        check("'help' lists the commands",
            helpCode == 0 && helpOutput.Contains("convert", StringComparison.Ordinal),
            $"exit {helpCode}");

        var (badCode, _, badError) = RunCommand(exe, ["wibble"]);
        check("an unknown command exits 2 and says so on stderr",
            badCode == 2 && badError.Contains("unknown command", StringComparison.Ordinal),
            $"exit {badCode}, stderr '{badError.Trim()}'");

        var (missingCode, _, _) = RunCommand(exe, ["info", "definitely-not-here.jpg"]);
        check("a missing file exits 1", missingCode == 1, $"exit {missingCode}");

        // A real conversion, end to end through the shipped binary.
        var scratch = Path.Combine(Path.GetTempPath(), "imageviewer-cli-e2e");
        Directory.CreateDirectory(scratch);

        var source = Path.Combine(scratch, "source.png");
        var target = Path.Combine(scratch, "converted.jpg");
        if (File.Exists(target)) File.Delete(target);

        WriteSolidPng(source, 300, 200);

        var (convertCode, convertOutput, convertError) = RunCommand(
            exe, ["convert", source, target, "--quality", "80"]);

        check("'convert' writes the target file",
            convertCode == 0 && File.Exists(target),
            $"exit {convertCode}, out '{convertOutput.Trim()}', err '{convertError.Trim()}'");

        if (File.Exists(target))
        {
            var (_, sizeOutput, _) = RunCommand(exe, ["info", target, "--quiet"]);
            check("the converted file reports the original dimensions",
                sizeOutput.Trim() == "300x200", $"got '{sizeOutput.Trim()}'");
        }

        // Refusing to clobber is the behaviour that makes a batch conversion safe to re-run.
        var (secondCode, _, secondError) = RunCommand(exe, ["convert", source, target]);
        check("converting over an existing file needs --overwrite",
            secondCode == 1 && secondError.Contains("already exists", StringComparison.Ordinal),
            $"exit {secondCode}, stderr '{secondError.Trim()}'");

        var (overwriteCode, _, _) = RunCommand(exe, ["convert", source, target, "--overwrite"]);
        check("--overwrite replaces it", overwriteCode == 0, $"exit {overwriteCode}");

        var (resizeCode, _, _) = RunCommand(
            exe, ["resize", source, Path.Combine(scratch, "small.jpg"), "--width", "100", "--overwrite"]);
        var (_, smallSize, _) = RunCommand(exe, ["info", Path.Combine(scratch, "small.jpg"), "--quiet"]);

        // 300x200 constrained to 100 wide is 66.67 high, so the exact rounding is the decoder's
        // business - asserting one of 66 or 67 would be pinning down an implementation detail.
        // What must hold is that the width is honoured and the shape is not distorted.
        var parts = smallSize.Trim().Split('x');
        var resizedWidth = parts.Length == 2 && int.TryParse(parts[0], out var w) ? w : 0;
        var resizedHeight = parts.Length == 2 && int.TryParse(parts[1], out var h) ? h : 0;

        check("'resize' constrains the width and keeps the aspect ratio",
            resizeCode == 0 && resizedWidth == 100 && Math.Abs(resizedHeight - 200 * 100 / 300.0) <= 1,
            $"exit {resizeCode}, got '{smallSize.Trim()}', expected 100x67 give or take a pixel");

        // Never enlarging is the default; a 300 px source asked to fit 2000 px stays 300 px.
        var (_, _, _) = RunCommand(
            exe, ["resize", source, Path.Combine(scratch, "big.jpg"), "--width", "2000", "--overwrite"]);
        var (_, bigSize, _) = RunCommand(exe, ["info", Path.Combine(scratch, "big.jpg"), "--quiet"]);

        check("resize does not enlarge unless asked",
            bigSize.Trim() == "300x200", $"got '{bigSize.Trim()}'");

        Console.WriteLine($"    driven: {exe}");
    }

    /// <summary>Writes a small PNG without going through the viewer's own decoder.</summary>
    private static void WriteSolidPng(string path, int width, int height)
    {
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;       // B
            pixels[i + 1] = 0x90;   // G
            pixels[i + 2] = 0xC0;   // R
            pixels[i + 3] = 0xFF;   // A
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, stride, 0);
        bitmap.Freeze();

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static (int ExitCode, string Output, string Error) RunCommand(string exe, string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = exe,
            // Redirection is what makes this work at all: the application is a WinExe, so without
            // it the output would go to a console that a test process does not have.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start {exe}");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{exe} did not exit");
        }

        return (process.ExitCode, output, error);
    }

    /// <summary>Looks for a built ImageViewer.exe matching this test's own configuration.</summary>
    private static string? FindViewerExecutable()
    {
        // Beside the test binary first: that is where a publish or a copied output would put it.
        var sibling = Path.Combine(AppContext.BaseDirectory, "ImageViewer.exe");
        if (File.Exists(sibling)) return sibling;

        // Otherwise walk up to the repository and mirror this assembly's configuration and TFM,
        // so a Debug test never drives a stale Release binary or the other way round.
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = baseDirectory.Name;
        var configuration = baseDirectory.Parent?.Name;

        if (configuration is null) return null;

        var walker = baseDirectory;
        while (walker is not null)
        {
            var candidate = Path.Combine(
                walker.FullName, "src", "ImageViewer", "bin", configuration, framework, "ImageViewer.exe");

            if (File.Exists(candidate)) return candidate;
            walker = walker.Parent;
        }

        return null;
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }
}
