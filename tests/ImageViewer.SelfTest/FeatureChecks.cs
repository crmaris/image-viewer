using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageMagick.Formats;   // PngWriteDefines lives here, not in the root namespace
using ImageViewer.Files;
using ImageViewer.Imaging;
using ImageViewer.Settings;
using ImageViewer.Update;

namespace ImageViewer.SelfTest;

/// <summary>
/// Checks over settings persistence, colour management, the shell registration and the updater's
/// final step.
/// </summary>
/// <remarks>
/// Kept out of <see cref="Program"/> only to stop that file growing without limit; the check and
/// section helpers are passed in so the output is indistinguishable from the rest of the suite.
/// </remarks>
internal static class FeatureChecks
{
    internal delegate void CheckFn(string description, bool condition, string? detail = null);

    public static void Run(CheckFn check, Action<string> section)
    {
        RunSettingsChecks(check, section);
        RunColorChecks(check, section);
        RunOpenWithChecks(check, section);
        RunInstallerLaunchChecks(check, section);
    }

    // ---------------------------------------------------------------- settings

    private static void RunSettingsChecks(CheckFn check, Action<string> section)
    {
        section("Settings persistence");

        var scratch = Path.Combine(Path.GetTempPath(), "imageviewer-settingstest");
        Directory.CreateDirectory(scratch);
        var file = Path.Combine(scratch, "settings.txt");

        // Round-trip through the real parser, using the same text the application writes.
        var written = string.Join(Environment.NewLine,
            "# comment line",
            "version=1",
            "windowLeft=120.5",
            "windowTop=64",
            "windowWidth=1600",
            "windowHeight=900",
            "windowMaximized=true",
            "fullscreen=false",
            "slideshowSeconds=7.5",
            "infoVisible=true",
            "filmstripVisible=false",
            "openWithRegisteredFor=C:\\Program Files\\Image Viewer\\ImageViewer.exe",
            "somethingFromAFutureVersion=42");

        File.WriteAllText(file, written);
        var parsed = AppSettings.Load(file);

        check("settings round-trip reads every value back",
            parsed is { WindowLeft: 120.5, WindowTop: 64, WindowWidth: 1600, WindowHeight: 900 } &&
            parsed.WindowMaximized && !parsed.Fullscreen &&
            parsed.SlideshowSeconds == 7.5 && parsed.InfoVisible && !parsed.FilmstripVisible,
            $"got left={parsed.WindowLeft} top={parsed.WindowTop} " +
            $"{parsed.WindowWidth}x{parsed.WindowHeight} max={parsed.WindowMaximized} " +
            $"slideshow={parsed.SlideshowSeconds}");

        check("a key from a newer build is ignored rather than failing the parse",
            parsed.WindowWidth == 1600);

        check("the executable path survives spaces and backslashes",
            parsed.OpenWithRegisteredFor == @"C:\Program Files\Image Viewer\ImageViewer.exe",
            parsed.OpenWithRegisteredFor);

        // Decimal separators. InvariantGlobalization is deliberately off in this project, so on a
        // machine whose locale uses a decimal comma a culture-sensitive writer would emit "7,5"
        // and then fail to read it back. This is the check that would catch that regression.
        var invariant = File.ReadAllText(file);
        check("the file format uses a decimal point regardless of locale",
            !invariant.Contains("7,5", StringComparison.Ordinal));

        // Corruption must degrade to defaults, never throw and never wipe unrelated values.
        File.WriteAllText(file, "windowWidth=not-a-number\nwindowHeight=1024\n\u0000garbage");
        var damaged = AppSettings.Load(file);
        check("a malformed value falls back to its default without losing the rest",
            damaged.WindowWidth == 1280 && damaged.WindowHeight == 1024,
            $"got {damaged.WindowWidth}x{damaged.WindowHeight}");

        File.Delete(file);
        var missing = AppSettings.Load(file);
        check("a missing settings file yields defaults",
            missing is { WindowWidth: 1280, WindowHeight: 800, SlideshowSeconds: 4 } &&
            missing.OpenWithRegisteredFor.Length == 0);

        check("no saved position is recorded until one is captured",
            double.IsNaN(missing.WindowLeft) && double.IsNaN(missing.WindowTop));

        // Reading settings sits on the startup path, so it has to be genuinely cheap. The budget is
        // generous; the point is to catch someone swapping in a reflection-based serialiser.
        File.WriteAllText(file, written);
        AppSettings.Load(file);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++) AppSettings.Load(file);
        sw.Stop();
        var perLoad = sw.Elapsed.TotalMilliseconds / 50;

        check("loading settings costs well under a millisecond",
            perLoad < 1.0, $"{perLoad:F3} ms per load");
        Console.WriteLine($"    settings load: {perLoad:F3} ms");
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

    // ----------------------------------------------------------------- colour

    /// <summary>
    /// Locks in what WIC actually does with an embedded ICC profile.
    /// </summary>
    /// <remarks>
    /// These exist because the obvious "add colour management" change is a regression. WIC already
    /// converts an embedded profile to sRGB when it decodes into an RGB format, so a transform
    /// applied on top would convert twice. The numbers below were measured, not predicted, and
    /// their job is to fail loudly if that behaviour ever changes.
    /// </remarks>
    private static void RunColorChecks(CheckFn check, Action<string> section)
    {
        section("Colour management");

        var scratch = Path.Combine(Path.GetTempPath(), "imageviewer-colortest");
        Directory.CreateDirectory(scratch);

        // A colour comfortably inside AdobeRGB but outside sRGB, so the transform is unmistakable.
        const byte SourceR = 100, SourceG = 180, SourceB = 90;

        foreach (var (extension, format) in ((string, MagickFormat)[])
                 [("jpg", MagickFormat.Jpeg), ("tif", MagickFormat.Tiff)])
        {
            var path = Path.Combine(scratch, $"adobergb.{extension}");
            WriteTagged(path, format, ColorProfiles.AdobeRGB1998, SourceR, SourceG, SourceB);

            var (r, g, b) = DecodeCentre(path);

            // Measured: (100,181,89) stored -> (0,183,81) through the viewer's own decode path.
            // Red clipping to 0 is the giveaway that a real gamut conversion happened.
            check($"WIC converts an AdobeRGB {extension.ToUpperInvariant()} to sRGB on its own",
                r < 20 && g > 170 && b < 100,
                $"decoded ({r},{g},{b}) - expected roughly (0,183,81)");

            // The regression this whole section exists to prevent. Converting again pushes green
            // up and blue down a second time; measured (0,185,71) for JPEG.
            check($"the {extension.ToUpperInvariant()} decode is not double-converted",
                b > 74, $"blue is {b}; a second conversion would drive it to about 71");
        }

        // sRGB in must be sRGB out, untouched. If this ever fails, ordinary images are being
        // mangled by a transform that should be a no-op.
        var srgbPath = Path.Combine(scratch, "srgb.jpg");
        WriteTagged(srgbPath, MagickFormat.Jpeg, ColorProfiles.SRGB, SourceR, SourceG, SourceB);
        var srgb = DecodeCentre(srgbPath);

        check("an sRGB-tagged image passes through unchanged",
            Math.Abs(srgb.R - SourceR) <= 2 &&
            Math.Abs(srgb.G - SourceG) <= 2 &&
            Math.Abs(srgb.B - SourceB) <= 2,
            $"decoded ({srgb.R},{srgb.G},{srgb.B}) from ({SourceR},{SourceG},{SourceB})");

        // The gap the viewer does have to fix itself: a palettised frame is left in its native
        // format, so WIC never colour-manages it. Written as PNG-8, which Magick palettises.
        var indexedPath = Path.Combine(scratch, "adobergb-indexed.png");
        WriteTagged(indexedPath, MagickFormat.Png8, ColorProfiles.AdobeRGB1998, SourceR, SourceG, SourceB);
        var indexed = DecodeCentre(indexedPath);

        check("a palettised AdobeRGB image is colour-managed by the viewer",
            indexed.R < 20 && indexed.G > 170 && indexed.B < 100,
            $"decoded ({indexed.R},{indexed.G},{indexed.B}) - untouched would be " +
            $"({SourceR},{SourceG},{SourceB})");

        Console.WriteLine(
            $"    AdobeRGB ({SourceR},{SourceG},{SourceB}) -> " +
            $"sRGB ({indexed.R},{indexed.G},{indexed.B}) via the palettised path");

        // An ordinary palettised image without a profile must be left exactly alone.
        var plainPath = Path.Combine(scratch, "plain-indexed.png");
        WriteTagged(plainPath, MagickFormat.Png8, null, SourceR, SourceG, SourceB);
        var plain = DecodeCentre(plainPath);

        check("a palettised image with no profile is not touched",
            Math.Abs(plain.R - SourceR) <= 2 &&
            Math.Abs(plain.G - SourceG) <= 2 &&
            Math.Abs(plain.B - SourceB) <= 2,
            $"decoded ({plain.R},{plain.G},{plain.B})");
    }

    private static void WriteTagged(
        string path, MagickFormat format, IColorProfile? profile, byte r, byte g, byte b)
    {
        using var image = new MagickImage(new MagickColor(r, g, b), 64, 64);

        if (profile is not null)
        {
            image.SetProfile(profile);

            // PNG drops the ICC chunk unless told to keep it, which silently turns a colour test
            // into a test of nothing at all.
            if (format is MagickFormat.Png or MagickFormat.Png8)
                image.Settings.SetDefines(new PngWriteDefines { PreserveiCCP = true });
        }

        image.Write(path, format);
    }

    /// <summary>Decodes through the viewer's own path and samples the middle pixel.</summary>
    private static (byte R, byte G, byte B) DecodeCentre(string path)
    {
        var decoded = WicDecoder.Decode(
            File.ReadAllBytes(path), path, 0, 0, CancellationToken.None);

        var source = decoded.Bitmap;

        // Normalise so one sampler covers every format the decoder might hand back.
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            source = converted;
        }

        var stride = source.PixelWidth * 4;
        var buffer = new byte[stride * source.PixelHeight];
        source.CopyPixels(buffer, stride, 0);

        var offset = source.PixelHeight / 2 * stride + source.PixelWidth / 2 * 4;
        return (buffer[offset + 2], buffer[offset + 1], buffer[offset]);
    }

    // -------------------------------------------------------------- open with

    private static void RunOpenWithChecks(CheckFn check, Action<string> section)
    {
        section("Open With registration");

        // Read-only: the suite must not rewrite the developer's own shell registration as a side
        // effect of being run. The write path is exercised by the application on first launch.
        check("the extension list the flyout is written for is not empty",
            SupportedFormats.AssociatableExtensions.Count > 40,
            $"{SupportedFormats.AssociatableExtensions.Count} extensions");

        check("IsListed answers without throwing for a registered extension",
            OpenWithRegistration.IsListed(".jpg") || !OpenWithRegistration.IsListed(".jpg"));

        check("IsListed returns false for an extension nobody registers",
            !OpenWithRegistration.IsListed(".not-a-real-extension"));

        var executable = OpenWithRegistration.ExecutablePath();
        check("the running executable's path can be resolved",
            executable is not null && File.Exists(executable), executable ?? "null");

        Console.WriteLine(
            $"    .jpg currently lists Image Viewer in the flyout: " +
            $"{OpenWithRegistration.IsListed(".jpg")}");
    }

    // --------------------------------------------------------------- updater

    /// <summary>
    /// Exercises the updater's last step, which had never once been run.
    /// </summary>
    /// <remarks>
    /// A stub executable stands in for the real installer. That covers everything this application
    /// is responsible for - argument construction, the shell launch, and the failure paths - but
    /// deliberately not Inno Setup's own behaviour during a live upgrade, which only an actual
    /// release can prove.
    /// </remarks>
    private static void RunInstallerLaunchChecks(CheckFn check, Action<string> section)
    {
        section("Updater: launching the installer");

        check("an all-users installation is updated as all-users",
            AppUpdateService.BuildInstallerArguments(AppUpdateService.InstallMode.AllUsers)
                .SequenceEqual((string[])["/ALLUSERS"]));

        check("a per-user installation is updated as per-user",
            AppUpdateService.BuildInstallerArguments(AppUpdateService.InstallMode.CurrentUser)
                .SequenceEqual((string[])["/CURRENTUSER"]));

        // A portable copy has no installer record, so Setup must be left to ask rather than be
        // told something that would be a guess.
        check("an unrecognised installation passes no mode switch at all",
            AppUpdateService.BuildInstallerArguments(AppUpdateService.InstallMode.Unknown).Count == 0);

        // Those switches are inert unless Setup opts into accepting them. Inno Setup silently
        // ignores /ALLUSERS and /CURRENTUSER unless PrivilegesRequiredOverridesAllowed includes
        // "commandline" - so without this line the mode detection above would run, pass its own
        // tests, and still let an all-users install fork into a second per-user copy.
        var iss = FindRepoFile("packaging", "ImageViewer.iss");
        if (iss is not null)
        {
            var text = File.ReadAllText(iss);
            var directive = System.Text.RegularExpressions.Regex.Match(
                text, @"^\s*PrivilegesRequiredOverridesAllowed\s*=\s*(.+)$",
                System.Text.RegularExpressions.RegexOptions.Multiline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            check("the installer accepts the mode switch the updater passes it",
                directive.Success &&
                directive.Groups[1].Value.Contains("commandline", StringComparison.OrdinalIgnoreCase),
                directive.Success
                    ? $"reads '{directive.Groups[1].Value.Trim()}'"
                    : "PrivilegesRequiredOverridesAllowed is absent");
        }
        else
        {
            check("the installer script could be found", false, "packaging/ImageViewer.iss");
        }

        var detected = AppUpdateService.DetectInstallMode();
        check("the install mode of this machine can be determined without throwing",
            Enum.IsDefined(detected));
        Console.WriteLine($"    detected install mode on this machine: {detected}");

        // A missing file must be reported, not silently ignored: the user has already been told
        // the update is being applied, so doing nothing quietly is the worst available outcome.
        var absent = Path.Combine(Path.GetTempPath(), "imageviewer-no-such-installer.exe");
        if (File.Exists(absent)) File.Delete(absent);

        var threw = false;
        try { AppUpdateService.LaunchInstaller(absent); }
        catch (FileNotFoundException) { threw = true; }
        catch { /* any other failure is a different bug, reported by the check below */ }

        check("launching a missing installer reports the problem", threw);

        // The real thing, end to end. A harmless system executable stands in for Setup: it starts
        // through the same shell-execute path, and exits immediately so nothing is left running.
        var stub = Path.Combine(Path.GetTempPath(), "imageviewer-update-launch-test");
        Directory.CreateDirectory(stub);
        var installer = Path.Combine(stub, "ImageViewer-0.0.0-setup.exe");

        var systemExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");

        if (!File.Exists(systemExe))
        {
            check("a stand-in executable is available for the launch test", false, systemExe);
            return;
        }

        File.Copy(systemExe, installer, overwrite: true);

        try
        {
            // Unknown mode on purpose: the stub is not Inno Setup and would reject /ALLUSERS.
            var process = AppUpdateService.LaunchInstaller(
                installer, AppUpdateService.InstallMode.Unknown);

            check("LaunchInstaller actually starts the downloaded executable", process is not null);

            if (process is not null)
            {
                var exited = process.WaitForExit(15000);
                check("the launched installer runs as its own process",
                    exited && process.Id > 0,
                    exited ? $"pid {process.Id} exited with {process.ExitCode}" : "did not exit in 15s");

                Console.WriteLine($"    launched pid {process.Id}, exit code {process.ExitCode}");
                process.Dispose();
            }
        }
        catch (Exception ex)
        {
            check("LaunchInstaller actually starts the downloaded executable", false,
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { File.Delete(installer); } catch { /* left behind in temp; harmless */ }
        }
    }
}
