using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Editing;
using ImageViewer.Imaging;
using ImageViewer.Navigation;
using ImageViewer.Update;

namespace ImageViewer.SelfTest;

/// <summary>
/// Headless checks over the decode, sort and view-transform logic, plus decode timings.
/// </summary>
/// <remarks>
/// Deliberately dependency-free rather than a test framework: it has to run against the same WPF
/// imaging stack the viewer uses, start fast enough to be run constantly during development, and
/// produce timing numbers that can be compared against the performance budget.
/// </remarks>
public static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    public static int Main(string[] args)
    {
        var mode = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal));
        var dir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                  ?? Path.Combine(AppContext.BaseDirectory, "testimages");

        // Live network check, kept out of the normal suite: a test that fails when the wifi drops
        // is worse than no test.
        if (mode == "--check-update")
        {
            return LiveUpdateCheck
                .RunAsync(args.Contains("--download"))
                .GetAwaiter().GetResult();
        }

        if (mode == "--make-corpus")
        {
            Console.WriteLine($"Generating exotic-format test files in {dir}");
            var count = Corpus.Generate(dir);
            Console.WriteLine($"Wrote {count} files.");
            return 0;
        }

        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"Test image folder not found: {dir}");
            Console.Error.WriteLine("Usage: ImageViewer.SelfTest [--assembly-check|--make-corpus] <folder>");
            return 2;
        }

        // Must be its own process: the normal suite exercises every tier, after which the question
        // "did the heavy tiers stay unloaded?" can no longer be asked.
        if (mode == "--assembly-check") return AssemblyLoadCheck.Run(dir);

        Console.WriteLine($"Test corpus: {dir}\n");

        RunCorrectnessChecks(dir);
        RunViewTransformChecks(dir);
        RunDecoderChainChecks(dir);
        RunOrientationChecks();
        RunSaveChecks(dir);
        RunPackagingChecks();
        RunUpdateChecks();
        RunCacheChecks(dir);
        RunPipelineChecks(dir);
        RunTimings(dir);

        Console.WriteLine();
        Console.WriteLine($"  {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- correctness

    private static void RunCorrectnessChecks(string dir)
    {
        Section("Decoding");

        // Natural sort: a numbered series must page in human order, not lexicographic order.
        var scanned = FolderScanner.ScanAsync(dir, CancellationToken.None).GetAwaiter().GetResult();
        var numbered = scanned
            .Select(Path.GetFileName)
            .Where(n => n is not null && n.StartsWith("img", StringComparison.Ordinal))
            .ToArray();

        Check("natural sort orders img1 < img2 < img3 < img10 < img20",
            numbered.SequenceEqual(["img1.png", "img2.png", "img3.png", "img10.png", "img20.png"]),
            $"got [{string.Join(", ", numbered)}]");

        Check("scanner finds the Greek-named file",
            scanned.Any(p => Path.GetFileName(p) == "δοκιμή-εικόνα.png"));

        Check("scanner excludes non-image files",
            scanned.All(p => SupportedFormats.IsSupported(p)));

        // EXIF orientation: WIC does not apply this on its own, so a viewer that skips the step
        // shows every rotated photo sideways. Quarter turns must swap the reported axes.
        var o6 = Decode(dir, "exif-orientation-6.jpg");
        Check("EXIF orientation 6 swaps 1200x800 to 800x1200",
            o6 is { PixelWidth: 800, PixelHeight: 1200 },
            $"got {o6?.PixelWidth}x{o6?.PixelHeight}");
        Check("EXIF orientation 6 rotates the actual pixels too",
            o6 is not null && o6.Bitmap.PixelWidth < o6.Bitmap.PixelHeight,
            $"bitmap is {o6?.Bitmap.PixelWidth}x{o6?.Bitmap.PixelHeight}");

        var o8 = Decode(dir, "exif-orientation-8.jpg");
        Check("EXIF orientation 8 swaps 1024x768 to 768x1024",
            o8 is { PixelWidth: 768, PixelHeight: 1024 },
            $"got {o8?.PixelWidth}x{o8?.PixelHeight}");

        // Decode-to-fit: the headline decode optimisation. The bitmap shrinks, the reported
        // dimensions do not, so zoom levels and the info overlay stay truthful.
        var big = Decode(dir, "large-6000x4000.jpg", 1920, 1080);
        Check("large JPEG reports its original 6000x4000",
            big is { PixelWidth: 6000, PixelHeight: 4000 },
            $"got {big?.PixelWidth}x{big?.PixelHeight}");
        Check("large JPEG actually decodes downscaled to fit 1920x1080",
            big is not null && big.Bitmap.PixelWidth <= 1920 && big.Bitmap.PixelHeight <= 1080,
            $"decoded {big?.Bitmap.PixelWidth}x{big?.Bitmap.PixelHeight}");
        Check("DecodeScale reflects the downscale",
            big is not null && big.DecodeScale is > 0.2 and < 0.3,
            $"DecodeScale={big?.DecodeScale:F3}");

        // Full-resolution path, used once the user zooms past what the downscale holds.
        var full = Decode(dir, "large-6000x4000.jpg", 0, 0);
        Check("full-resolution decode returns all 6000 pixels",
            full is not null && full.Bitmap.PixelWidth == 6000 && full.DecodeScale == 1.0,
            $"decoded {full?.Bitmap.PixelWidth} wide, scale {full?.DecodeScale}");

        // Small images must not be upscaled by the decoder.
        var tiny = Decode(dir, "tiny-64.png", 1920, 1080);
        Check("64px image is not enlarged during decode",
            tiny is { Bitmap.PixelWidth: 64 },
            $"got {tiny?.Bitmap.PixelWidth}");

        // Content sniffing: WIC keys off magic bytes, so a wrong extension still opens.
        var mislabelled = Decode(dir, "mislabelled-actually-png.jpg");
        Check("PNG bytes with a .jpg extension still decode",
            mislabelled is not null,
            "decoder refused the mislabelled file");

        Check("Greek filename decodes",
            Decode(dir, "δοκιμή-εικόνα.png") is not null);

        // Robustness: a broken file must surface an error, never take the process down.
        var threw = false;
        try { Decode(dir, "corrupt.jpg", throwOnError: true); }
        catch { threw = true; }
        Check("corrupt file throws a catchable error rather than crashing", threw);

        // Every other format in the corpus should simply work.
        foreach (var name in (string[])["sample.bmp", "sample.gif", "sample.tif", "sample.jpg", "portrait-900x2400.jpg"])
            Check($"{name} decodes", Decode(dir, name) is not null);

        // Embedded thumbnails are the instant-first-paint trick.
        var thumbPath = Path.Combine(dir, "with-embedded-thumb.jpg");
        if (File.Exists(thumbPath))
        {
            var thumbBytes = File.ReadAllBytes(thumbPath);
            var thumb = WicDecoder.TryDecodeEmbeddedThumbnail(thumbBytes, thumbPath, CancellationToken.None);

            Check("embedded thumbnail is found and decoded",
                thumb is not null,
                "TryDecodeEmbeddedThumbnail returned null");

            Check("thumbnail is flagged as a preview so it is not cached as the real decode",
                thumb is { IsPreview: true });

            Check("thumbnail reports the FULL frame's dimensions, so the swap does not jump",
                thumb is { PixelWidth: 6000, PixelHeight: 4000 },
                $"got {thumb?.PixelWidth}x{thumb?.PixelHeight}");

            Check("thumbnail bitmap really is small",
                thumb is not null && thumb.Bitmap.PixelWidth <= 320,
                $"bitmap {thumb?.Bitmap.PixelWidth}px wide");
        }
        else
        {
            Console.WriteLine("    note: with-embedded-thumb.jpg absent, thumbnail path not covered");
        }

        // A file with no thumbnail must return null rather than throwing - most PNGs have none.
        var noThumbBytes = File.ReadAllBytes(Path.Combine(dir, "img1.png"));
        var absent = WicDecoder.TryDecodeEmbeddedThumbnail(noThumbBytes, "img1.png", CancellationToken.None);
        Check("missing thumbnail returns null instead of throwing", absent is null);
    }

    // -------------------------------------------------------- view transform

    private static void RunViewTransformChecks(string dir)
    {
        Section("View transform");

        var landscape = Decode(dir, "large-6000x4000.jpg", 1920, 1080)!;
        var portrait = Decode(dir, "portrait-900x2400.jpg")!;
        var tiny = Decode(dir, "tiny-64.png")!;

        var view = new ViewTransform();
        var viewport = new Size(1280, 800);

        // 6000x4000 into 1280x800 is height-constrained: 800/4000 = 0.2 beats 1280/6000 = 0.213.
        var fit = view.ComputeFitZoom(landscape, viewport, dpiScale: 1.0);
        Check("fit zoom constrains on the limiting axis (0.20)",
            Math.Abs(fit - 0.2) < 0.001, $"got {fit:F4}");

        var fitPortrait = view.ComputeFitZoom(portrait, viewport, dpiScale: 1.0);
        Check("portrait fit constrains on height (800/2400)",
            Math.Abs(fitPortrait - 800.0 / 2400) < 0.001, $"got {fitPortrait:F4}");

        Check("small image fit is capped at 100% rather than enlarged",
            Math.Abs(view.ComputeFitZoom(tiny, viewport, 1.0) - 1.0) < 0.0001);

        // On a 150% display the viewport holds 1.5x more physical pixels, so more image fits.
        var fitHiDpi = view.ComputeFitZoom(landscape, viewport, dpiScale: 1.5);
        Check("fit accounts for DPI scaling",
            fitHiDpi > fit, $"100%={fit:F4} 150%={fitHiDpi:F4}");

        // Rotation swaps which axis binds.
        view.Rotate(90);
        var fitRotated = view.ComputeFitZoom(landscape, viewport, 1.0);
        Check("rotating 90 degrees re-derives fit against the swapped axes",
            Math.Abs(fitRotated - Math.Min(1280.0 / 4000, 800.0 / 6000)) < 0.001,
            $"got {fitRotated:F4}");
        view.Reset();

        // Zoom-at-cursor must keep the anchored image point under the cursor.
        view.ResolveZoom(landscape, viewport, 1.0);
        var anchor = new Point(300, 220);
        var before = view.BuildMatrix(landscape, viewport, 1.0);
        var inverse = before; inverse.Invert();
        var imagePoint = inverse.Transform(anchor);

        view.ZoomAt(2.0, anchor, landscape, viewport, 1.0);
        var after = view.BuildMatrix(landscape, viewport, 1.0);
        var moved = after.Transform(imagePoint);

        Check("zoom keeps the point under the cursor pinned",
            (moved - anchor).Length < 0.5,
            $"drifted {(moved - anchor).Length:F2} px");

        // Pan clamping keeps the image reachable.
        view.Reset();
        view.ResolveZoom(landscape, viewport, 1.0);
        view.Pan(99999, 99999, landscape, viewport, 1.0);
        Check("pan is clamped when the image fits entirely in the window",
            view.PanX == 0 && view.PanY == 0,
            $"PanX={view.PanX} PanY={view.PanY}");

        // Flip while quarter-turned has to act on the axis the user actually sees.
        view.Reset();
        view.Rotate(90);
        view.ToggleFlipHorizontal();
        Check("flip-horizontal while rotated 90 flips the on-screen horizontal axis",
            view.FlipVertical && !view.FlipHorizontal);
    }

    // ----------------------------------------------------------- decoder chain

    private static void RunDecoderChainChecks(string dir)
    {
        Section("Format sniffing");

        // Sniffing is what makes a mislabelled file open: the extension is never consulted.
        var cases = new (string File, ImageFormatKind Expected)[]
        {
            ("img1.png", ImageFormatKind.Png),
            ("sample.jpg", ImageFormatKind.Jpeg),
            ("sample.bmp", ImageFormatKind.Bmp),
            ("sample.gif", ImageFormatKind.Gif),
            ("sample.tif", ImageFormatKind.Tiff),
            ("mislabelled-actually-png.jpg", ImageFormatKind.Png),
            ("vector.svg", ImageFormatKind.Svg),
            ("exotic.tga", ImageFormatKind.Targa),
            ("exotic.psd", ImageFormatKind.Psd),
            ("exotic.webp", ImageFormatKind.Webp),
            ("exotic.exr", ImageFormatKind.OpenExr),
            ("exotic.qoi", ImageFormatKind.Qoi),
            ("exotic.ppm", ImageFormatKind.Pnm),
            ("exotic.jp2", ImageFormatKind.Jpeg2000),
        };

        foreach (var (file, expected) in cases)
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;

            var header = new byte[FormatSniffer.HeaderBytes];
            using (var fs = File.OpenRead(path)) fs.ReadExactly(header, 0, Math.Min(header.Length, (int)fs.Length));

            var actual = FormatSniffer.Identify(header);
            Check($"{file} sniffs as {expected}", actual == expected, $"got {actual}");
        }

        var mislabelledPath = Path.Combine(dir, "mislabelled-actually-png.jpg");
        Check("a .jpg that is really a PNG is identified by content, not extension",
            FormatSniffer.Identify(File.ReadAllBytes(mislabelledPath), mislabelledPath)
                == ImageFormatKind.Png);

        // Gzipped SVG has no distinguishing header, so the extension is the only signal. This is
        // the case the extension-hint fallback exists for.
        var svgzPath = Path.Combine(dir, "vector.svgz");
        if (File.Exists(svgzPath))
        {
            Check("gzipped .svgz is unidentifiable from its header alone",
                FormatSniffer.Identify(File.ReadAllBytes(svgzPath)) == ImageFormatKind.Unknown);

            Check("gzipped .svgz is resolved as SVG once the extension is considered",
                FormatSniffer.Identify(File.ReadAllBytes(svgzPath), svgzPath) == ImageFormatKind.Svg);
        }

        Section("Decoder chain (fallback tiers)");

        // Each of these needs a tier past WIC, so reaching pixels proves the fallback works.
        var exotic = new[]
        {
            "exotic.psd", "exotic.tga", "exotic.pcx", "exotic.ppm", "exotic.pgm",
            "exotic.exr", "exotic.hdr", "exotic.jp2", "exotic.webp", "exotic.qoi",
            "vector.svg", "vector.svgz", "animated.gif",
        };

        foreach (var name in exotic)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                Console.WriteLine($"    skip  {name} (not generated)");
                continue;
            }

            try
            {
                var img = DecoderChain.Decode(
                    File.ReadAllBytes(path), path, 1920, 1080, CancellationToken.None);

                Check($"{name} decodes via {img.DecoderName}",
                    img.Bitmap.PixelWidth > 0 && img.Bitmap.PixelHeight > 0,
                    "produced a zero-sized bitmap");
            }
            catch (Exception ex)
            {
                Check($"{name} decodes", false, ex.Message.Split('\n')[0]);
            }
        }

        // A vector should rasterise to fill the viewport rather than its declared 400x300, since
        // enlarging a vector costs no quality.
        var svgPath = Path.Combine(dir, "vector.svg");
        if (File.Exists(svgPath))
        {
            var big = DecoderChain.Decode(
                File.ReadAllBytes(svgPath), svgPath, 1600, 1200, CancellationToken.None);
            Check("SVG rasterises up to the requested viewport",
                big.Bitmap.PixelWidth > 400,
                $"only rendered {big.Bitmap.PixelWidth}px wide");
        }

        // Animated GIF: WPF shows only the first frame on its own, so the animator has to composite.
        var gifPath = Path.Combine(dir, "animated.gif");
        if (File.Exists(gifPath))
        {
            using var animator = GifAnimator.TryCreate(
                File.ReadAllBytes(gifPath), CancellationToken.None);

            Check("animated GIF produces an animator", animator is not null);
            Check("all four frames are found", animator?.FrameCount == 4, $"got {animator?.FrameCount}");
        }

        // A still image must not be treated as an animation.
        using (var notAnimated = GifAnimator.TryCreate(
                   File.ReadAllBytes(Path.Combine(dir, "sample.gif")), CancellationToken.None))
        {
            Check("single-frame GIF is correctly not animated", notAnimated is null);
        }

        // The failure message has to be diagnosable, not just "could not display".
        try
        {
            DecoderChain.Decode([0x00, 0x01, 0x02, 0x03, 0x04], "junk.bin", 100, 100, CancellationToken.None);
            Check("undecodable data throws", false, "no exception raised");
        }
        catch (Exception ex)
        {
            Check("undecodable data throws with a message naming the tiers that were tried",
                ex.Message.Contains("Magick", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("recognised", StringComparison.OrdinalIgnoreCase),
                $"message was: {ex.Message.Split('\n')[0]}");
        }
    }

    // ------------------------------------------------------------------ update

    private static void RunUpdateChecks()
    {
        Section("Auto-update");

        // Tag parsing has to cope with however the release was named.
        var tags = new (string Tag, string? Expected)[]
        {
            ("v0.2.0", "0.2.0.0"),
            ("0.2.0", "0.2.0.0"),
            ("V1.10.3", "1.10.3.0"),
            ("v0.2", "0.2.0.0"),
            ("v1.0.0-beta.2", "1.0.0.0"),   // pre-release suffix stripped
            ("not-a-version", null),
            ("", null),
        };

        foreach (var (tag, expected) in tags)
        {
            var parsed = AppUpdateService.ParseVersion(tag);
            Check($"tag '{tag}' parses to {expected ?? "nothing"}",
                parsed?.ToString() == expected,
                $"got {parsed?.ToString() ?? "null"}");
        }

        // "0.2" and "0.2.0.0" must compare equal, or every check would report a phantom update.
        Check("short and long forms of the same version compare equal",
            AppUpdateService.ParseVersion("v0.2") == AppUpdateService.ParseVersion("0.2.0.0"));

        Check("a newer tag compares greater than the running version",
            AppUpdateService.ParseVersion("v99.0.0") > AppUpdateService.CurrentVersion);

        Check("the current tag does not compare as an update",
            AppUpdateService.ParseVersion($"v{AppUpdateService.CurrentVersion.ToString(3)}")
                <= AppUpdateService.CurrentVersion);

        // The updater downloads and then EXECUTES a file, so the host allow-list is the thing
        // standing between a tampered API response and running an arbitrary binary.
        var allowed = new[]
        {
            "https://github.com/crmaris/image-viewer/releases/download/v1/ImageViewer-setup.exe",
            "https://objects.githubusercontent.com/some/path/setup.exe",
        };

        foreach (var url in allowed)
            Check($"accepts {new Uri(url).Host}", AppUpdateService.IsAllowedDownload(url));

        var rejected = new[]
        {
            ("http://github.com/x/y/setup.exe", "plain HTTP"),
            ("https://evil.example.com/setup.exe", "an unrelated host"),
            ("https://github.com.evil.example.com/setup.exe", "a lookalike domain"),
            ("file:///C:/Windows/System32/calc.exe", "a local file path"),
            ("ftp://github.com/setup.exe", "a non-HTTPS scheme"),
            ("not a url at all", "malformed input"),
        };

        foreach (var (url, why) in rejected)
            Check($"rejects {why}", !AppUpdateService.IsAllowedDownload(url), url);

        Check("the update service names a repository",
            !string.IsNullOrWhiteSpace(AppUpdateService.RepositoryOwner) &&
            !string.IsNullOrWhiteSpace(AppUpdateService.RepositoryName));

        Console.WriteLine($"    current version {AppUpdateService.CurrentVersion.ToString(3)}, " +
                          $"updates from {AppUpdateService.RepositoryOwner}/{AppUpdateService.RepositoryName}");
    }

    // --------------------------------------------------------------- packaging

    /// <summary>
    /// Checks the installer's file associations still match the code's list.
    /// </summary>
    /// <remarks>
    /// The two lists live in different languages and cannot share a definition, so they are kept in
    /// step by detection rather than by construction: adding a format to SupportedFormats without
    /// touching the .iss would otherwise silently ship an installer that does not offer it.
    /// </remarks>
    private static void RunPackagingChecks()
    {
        Section("Packaging");

        var iss = FindRepoFile("packaging", "ImageViewer.iss");
        if (iss is null)
        {
            Console.WriteLine("    skip  ImageViewer.iss not found (running outside the repo)");
            return;
        }

        var text = File.ReadAllText(iss);

        // Pull every extension out of the OpenWithProgids registry lines.
        var declared = System.Text.RegularExpressions.Regex
            .Matches(text, @"Software\\Classes\\(\.[a-z0-9]+)\\OpenWithProgids",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToHashSet();

        var expected = SupportedFormats.AssociatableExtensions
            .Select(e => e.ToLowerInvariant())
            .ToHashSet();

        var missing = expected.Except(declared).Order().ToArray();
        var extra = declared.Except(expected).Order().ToArray();

        Check($"installer declares all {expected.Count} associatable extensions",
            missing.Length == 0,
            missing.Length > 0 ? $"missing from the .iss: {string.Join(", ", missing)}" : null);

        Check("installer declares no extensions the code does not support",
            extra.Length == 0,
            extra.Length > 0 ? $"extra in the .iss: {string.Join(", ", extra)}" : null);

        // Associations must be additive; seizing the default handler is both hostile and blocked
        // by Windows 10 and 11 anyway.
        Check("installer does not try to seize the default file handler",
            !text.Contains(@"\shell\open\command"" ; ValueType", StringComparison.OrdinalIgnoreCase) &&
            !System.Text.RegularExpressions.Regex.IsMatch(
                text, @"Software\\Classes\\\.[a-z0-9]+""\s*;\s*ValueType:\s*string;\s*ValueName:\s*"""";",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "the .iss appears to set a default handler for an extension");

        // Without ChangesAssociations, Setup never calls SHChangeNotify, so Explorer keeps serving
        // cached association data and the app does not appear under "Open with" until sign-out.
        // Observed for real: 55 correct registry entries and still invisible in the menu.
        Check("installer declares ChangesAssociations so the shell is notified",
            System.Text.RegularExpressions.Regex.IsMatch(
                text, @"^\s*ChangesAssociations\s*=\s*yes",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Multiline),
            "Setup will not refresh Explorer's association cache");

        // SupportedTypes is the other half: the shell consults it when building the Open With list.
        Check("installer registers SupportedTypes for the application",
            text.Contains("SupportedTypes", StringComparison.Ordinal),
            "the app will not be offered in the Open With menu");

        // The code-generated SupportedTypes list must cover the same extensions as [Registry].
        var supportedBlock = System.Text.RegularExpressions.Regex.Match(
            text, @"SupportedExtensions\s*=(.*?);", System.Text.RegularExpressions.RegexOptions.Singleline);

        if (supportedBlock.Success)
        {
            var codeExtensions = System.Text.RegularExpressions.Regex
                .Matches(supportedBlock.Value, @"\.[a-z0-9]+")
                .Select(m => m.Value.ToLowerInvariant())
                .ToHashSet();

            var missingFromCode = expected.Except(codeExtensions).Order().ToArray();
            Check("the SupportedTypes list covers every associatable extension",
                missingFromCode.Length == 0,
                missingFromCode.Length > 0 ? $"missing: {string.Join(", ", missingFromCode)}" : null);
        }
        else
        {
            Check("the SupportedTypes extension list could be parsed", false, "SupportedExtensions not found");
        }

        Check("installer references the generated icon",
            text.Contains("app.ico", StringComparison.OrdinalIgnoreCase));

        var icon = FindRepoFile("src", "ImageViewer", "app.ico");
        Check("app.ico exists and holds several sizes",
            icon is not null && new FileInfo(icon).Length > 10_000,
            icon is null ? "not found" : $"only {new FileInfo(icon).Length} bytes");
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

    // ------------------------------------------------------------- orientation

    private static void RunOrientationChecks()
    {
        Section("Orientation algebra");

        // Round-trip every EXIF value.
        for (var exif = 1; exif <= 8; exif++)
        {
            Check($"EXIF {exif} round-trips through Orientation",
                Orientation.FromExif(exif).ToExif() == exif,
                $"became {Orientation.FromExif(exif).ToExif()}");
        }

        // Four quarter turns is the identity.
        var spun = Orientation.Identity;
        for (var i = 0; i < 4; i++) spun = spun.Then(new Orientation(false, 90));
        Check("four 90-degree turns compose to the identity", spun.IsIdentity, $"got {spun}");

        // Mirroring twice is the identity.
        var mirrored = new Orientation(true, 0).Then(new Orientation(true, 0));
        Check("mirroring twice composes to the identity", mirrored.IsIdentity, $"got {mirrored}");

        // The case the type exists for: a mirror reverses a rotation that came before it, so
        // naively adding rotations gives the wrong answer.
        var tricky = new Orientation(false, 90).Then(new Orientation(true, 0));
        Check("mirror after a quarter turn reverses that turn",
            tricky == new Orientation(true, 270),
            $"got {tricky}, naive addition would give (true, 90)");

        // Both flips together are a half turn with no mirror at all.
        Check("flipping both axes equals a 180-degree rotation",
            Orientation.FromUserEdits(true, true, 0) == new Orientation(false, 180),
            $"got {Orientation.FromUserEdits(true, true, 0)}");

        Check("a vertical flip is a horizontal flip plus a half turn",
            Orientation.FromUserEdits(false, true, 0) == new Orientation(true, 180));

        // A photo stored sideways (EXIF 6) that the user then turns back upright must end up
        // needing no transform at all.
        var corrected = Orientation.FromExif(6).Then(Orientation.FromUserEdits(false, false, 270));
        Check("EXIF 6 plus a 270-degree user turn cancels out",
            corrected.IsIdentity, $"got {corrected}");

        Check("quarter turns are reported as swapping the axes",
            new Orientation(false, 90).SwapsAxes && !new Orientation(false, 180).SwapsAxes);
    }

    // -------------------------------------------------------------------- save

    private static void RunSaveChecks(string dir)
    {
        Section("Saving (lossless JPEG verification)");

        var scratch = Path.Combine(Path.GetTempPath(), "imageviewer-savetest");
        Directory.CreateDirectory(scratch);

        // THE key check flagged during planning. The default JPEG path must be genuinely lossless:
        // rotate a full turn, then compare both the pixels and the raw bytes with the original.
        var original = Path.Combine(dir, "sample.jpg");
        var probe = Path.Combine(scratch, "roundtrip.jpg");
        File.Copy(original, probe, overwrite: true);

        var originalPixels = ReadPixels(probe);
        var originalFileBytes = File.ReadAllBytes(probe);

        for (var i = 0; i < 4; i++)
            ImageSaver.Save(probe, false, false, 90, CancellationToken.None);

        var afterPixels = ReadPixels(probe);
        var afterFileBytes = File.ReadAllBytes(probe);

        Check("a JPEG rotated through a full turn is pixel-identical to the original",
            originalPixels.AsSpan().SequenceEqual(afterPixels),
            $"{CountDifferences(originalPixels, afterPixels)} of {originalPixels.Length} bytes differ");

        // The first save on a JPEG with no EXIF block has to insert one, so a small fixed growth is
        // expected and is still lossless. What must never happen is the file changing size again,
        // which would mean the image data was being rewritten rather than the tag patched.
        var growth = afterFileBytes.Length - originalFileBytes.Length;
        Check("inserting an EXIF block costs only a small fixed header, not a re-encode",
            growth is >= 0 and < 128, $"file grew by {growth} bytes");

        var sizeBeforeRepeat = new FileInfo(probe).Length;
        for (var i = 0; i < 8; i++)
            ImageSaver.Save(probe, false, false, 90, CancellationToken.None);
        var sizeAfterRepeat = new FileInfo(probe).Length;

        Check("repeated rotations patch in place and never change the file size",
            sizeAfterRepeat == sizeBeforeRepeat,
            $"{sizeBeforeRepeat:N0} became {sizeAfterRepeat:N0} after 8 more rotations");

        Check("pixels are still identical after twelve rotations in total",
            originalPixels.AsSpan().SequenceEqual(ReadPixels(probe)),
            "the image degraded over repeated saves");

        Console.WriteLine($"    lossless path: {originalFileBytes.Length:N0} bytes -> " +
                          $"{sizeAfterRepeat:N0} after 12 rotations (+{growth} for the EXIF header)");

        // Record the finding that forced this design: WPF's encoder is NOT a block transform,
        // despite its reputation. If this ever starts passing, the simpler approach became viable.
        var encoderProbe = Path.Combine(scratch, "encoder.jpg");
        File.Copy(original, encoderProbe, overwrite: true);
        var beforeEncoder = ReadPixels(encoderProbe);
        for (var i = 0; i < 4; i++)
            ImageSaver.Save(encoderProbe, false, false, 90, CancellationToken.None, forceReEncode: true);

        var afterEncoder = ReadPixels(encoderProbe);
        var encoderDiffs = CountDifferences(beforeEncoder, afterEncoder);
        Check("re-encoding is confirmed lossy, which is why it is not the default",
            encoderDiffs > 0,
            "re-encode was lossless after all - the EXIF-only path may no longer be necessary");
        Console.WriteLine($"    re-encode path: {encoderDiffs:N0} of {beforeEncoder.Length:N0} bytes changed");

        // Dimensions must change on a quarter turn - as presented to a viewer that honours EXIF.
        var portraitProbe = Path.Combine(scratch, "quarter.jpg");
        File.Copy(original, portraitProbe, overwrite: true);
        var (beforeW, beforeH) = DecodeDisplayed(portraitProbe);
        ImageSaver.Save(portraitProbe, false, false, 90, CancellationToken.None);
        var (afterW, afterH) = DecodeDisplayed(portraitProbe);

        Check("a quarter turn swaps the displayed width and height",
            afterW == beforeH && afterH == beforeW,
            $"{beforeW}x{beforeH} became {afterW}x{afterH}");

        // A JPEG with no EXIF block at all needs one inserted, not a silent no-op.
        var noExif = Path.Combine(scratch, "noexif.jpg");
        File.Copy(Path.Combine(dir, "large-6000x4000.jpg"), noExif, overwrite: true);
        ImageSaver.Save(noExif, false, false, 90, CancellationToken.None);
        Check("a JPEG with no EXIF block gets one inserted",
            ReadExifTag(noExif) == 6, $"tag reads {ReadExifTag(noExif)}");
        Check("the inserted-EXIF file is still a valid decodable JPEG",
            DecodeDisplayed(noExif) is { Width: 4000, Height: 6000 },
            $"decoded as {DecodeDisplayed(noExif).Width}x{DecodeDisplayed(noExif).Height}");

        // A photo already carrying EXIF 6 must end up rotated by exactly what was asked, not by
        // the user's edit applied on top of a re-applied EXIF rotation. This is the double-rotation
        // bug that composing the two transforms exists to prevent.
        var exifProbe = Path.Combine(scratch, "exif6.jpg");
        File.Copy(Path.Combine(dir, "exif-orientation-6.jpg"), exifProbe, overwrite: true);

        var displayedBefore = DecodeDisplayed(exifProbe);
        ImageSaver.Save(exifProbe, false, false, 90, CancellationToken.None);
        var displayedAfter = DecodeDisplayed(exifProbe);

        Check("an already-EXIF-rotated photo rotates by exactly the amount asked for",
            displayedAfter.Width == displayedBefore.Height &&
            displayedAfter.Height == displayedBefore.Width,
            $"{displayedBefore.Width}x{displayedBefore.Height} became " +
            $"{displayedAfter.Width}x{displayedAfter.Height}");

        // EXIF 6 (90) plus a further 90 is 180, which is tag 3.
        Check("composing EXIF 6 with a further quarter turn yields EXIF 3",
            ReadExifTag(exifProbe) == 3, $"tag is {ReadExifTag(exifProbe)}");

        // A flip must not be turned into the wrong orientation by naive rotation arithmetic.
        var flipProbe = Path.Combine(scratch, "flip.jpg");
        File.Copy(Path.Combine(dir, "exif-orientation-6.jpg"), flipProbe, overwrite: true);
        ImageSaver.Save(flipProbe, true, false, 0, CancellationToken.None);
        var expectedFlip = Orientation.FromExif(6).Then(Orientation.FromUserEdits(true, false, 0)).ToExif();
        Check("flipping an EXIF-rotated photo composes correctly",
            ReadExifTag(flipProbe) == expectedFlip,
            $"tag is {ReadExifTag(flipProbe)}, expected {expectedFlip}");

        // PNG goes down the re-encode path, which is lossless for PNG anyway.
        var pngProbe = Path.Combine(scratch, "probe.png");
        File.Copy(Path.Combine(dir, "img1.png"), pngProbe, overwrite: true);
        var pngPixelsBefore = ReadPixels(pngProbe);
        var pngResult = ImageSaver.Save(pngProbe, false, false, 180, CancellationToken.None);
        Check("PNG saves via the re-encode path", pngResult.Method == SaveMethod.ReEncoded);

        ImageSaver.Save(pngProbe, false, false, 180, CancellationToken.None);
        Check("PNG survives a full turn unchanged, since PNG re-encoding is lossless",
            pngPixelsBefore.AsSpan().SequenceEqual(ReadPixels(pngProbe)),
            $"{CountDifferences(pngPixelsBefore, ReadPixels(pngProbe))} bytes differ");

        var jpegResult = ImageSaver.Save(probe, false, false, 90, CancellationToken.None);
        Check("JPEG defaults to the lossless path", jpegResult.Method == SaveMethod.LosslessExif);

        Check("saving with no edits reports nothing to do",
            ImageSaver.Save(probe, false, false, 0, CancellationToken.None).Method == SaveMethod.NoChange);

        // Formats WPF cannot write must say so rather than silently corrupting anything.
        var webp = Path.Combine(dir, "exotic.webp");
        if (File.Exists(webp))
        {
            var refused = false;
            try { ImageSaver.Save(webp, false, false, 90, CancellationToken.None); }
            catch (NotSupportedException) { refused = true; }
            Check("an unwritable format is refused rather than mangled", refused);
        }

        try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] ReadPixels(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var buffer = new byte[(long)stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    private static int CountDifferences(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return Math.Max(a.Length, b.Length);
        var count = 0;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) count++;
        return count;
    }

    private static (int Width, int Height) ReadSize(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
    }

    private static (int Width, int Height) DecodeDisplayed(string path)
    {
        var img = WicDecoder.Decode(File.ReadAllBytes(path), path, 0, 0, CancellationToken.None);
        return (img.PixelWidth, img.PixelHeight);
    }

    private static int ReadExifTag(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return WicDecoder.ReadExifOrientation(decoder.Frames[0]);
    }

    // ------------------------------------------------------------------- cache

    private static void RunCacheChecks(string dir)
    {
        Section("Cache");

        var images = new[] { "img1.png", "img2.png", "img3.png", "img10.png", "img20.png" }
            .Select(n => Decode(dir, n))
            .Where(i => i is not null)
            .Select(i => i!)
            .ToArray();

        if (images.Length < 4)
        {
            Check("cache checks have enough test images", false, $"only {images.Length} decoded");
            return;
        }

        var perImage = images[0].ApproximateBytes;

        // Budget deliberately set to hold roughly two and a half images, so eviction must happen.
        var cache = new ImageCache(budgetBytes: perImage * 5 / 2);

        foreach (var img in images) cache.Put(img);

        Check("cache never exceeds its byte budget",
            cache.CurrentBytes <= cache.BudgetBytes,
            $"{cache.CurrentBytes:N0} > {cache.BudgetBytes:N0}");

        Check("cache evicted down to what the budget allows",
            cache.Count is >= 1 and <= 3,
            $"held {cache.Count} of {images.Length}");

        // LRU: the most recent survives, the oldest is gone.
        Check("most recently added entry survives eviction",
            cache.TryGet(images[^1].Path, out _));

        Check("least recently used entry was evicted",
            !cache.TryGet(images[0].Path, out _));

        // Touching an entry must protect it from the next eviction.
        var fresh = new ImageCache(budgetBytes: perImage * 5 / 2);
        fresh.Put(images[0]);
        fresh.Put(images[1]);
        fresh.TryGet(images[0].Path, out _);   // promote the older one
        fresh.Put(images[2]);
        Check("a cache hit promotes an entry so it outlives an untouched newer one",
            fresh.TryGet(images[0].Path, out _),
            "the promoted entry was evicted anyway");

        // A request needing full resolution must not be handed a downscaled entry.
        var scaleCache = new ImageCache(budgetBytes: 512L << 20);
        var downscaled = Decode(dir, "large-6000x4000.jpg", 800, 600)!;
        scaleCache.Put(downscaled);
        Check("downscaled entry satisfies a fit-sized request",
            scaleCache.TryGet(downscaled.Path, out _));
        Check("downscaled entry is refused when full resolution is required",
            !scaleCache.TryGet(downscaled.Path, out _, minimumDecodeScale: 1.0));

        // Previews are placeholders; caching one would let a blurry thumbnail pose as the real image.
        var thumbPath = Path.Combine(dir, "with-embedded-thumb.jpg");
        if (File.Exists(thumbPath))
        {
            var preview = WicDecoder.TryDecodeEmbeddedThumbnail(
                File.ReadAllBytes(thumbPath), thumbPath, CancellationToken.None);
            var pc = new ImageCache(budgetBytes: 64L << 20);
            if (preview is not null) pc.Put(preview);
            Check("previews are not cached", pc.Count == 0, $"cache holds {pc.Count}");
        }

        var inv = new ImageCache(budgetBytes: 512L << 20);
        inv.Put(images[0]);
        inv.Invalidate(images[0].Path);
        Check("Invalidate removes an entry", !inv.TryGet(images[0].Path, out _));
    }

    // ---------------------------------------------------------------- pipeline

    private static void RunPipelineChecks(string dir)
    {
        Section("Pipeline and prefetch");

        using var pipeline = new ImagePipeline();
        var path = Path.Combine(dir, "large-6000x4000.jpg");

        Check("nothing is cached before the first request",
            !pipeline.TryGetCached(path, out _));

        var cold = Stopwatch.StartNew();
        var first = pipeline.GetAsync(path, 1920, 1080, CancellationToken.None)
            .GetAwaiter().GetResult();
        cold.Stop();

        Check("first request decodes the image", first is not null);
        Check("the decode is cached afterwards", pipeline.TryGetCached(path, out _));

        var warm = Stopwatch.StartNew();
        pipeline.GetAsync(path, 1920, 1080, CancellationToken.None).GetAwaiter().GetResult();
        warm.Stop();

        Check("a cached request is far cheaper than the cold decode",
            warm.Elapsed.TotalMilliseconds < cold.Elapsed.TotalMilliseconds / 4,
            $"cold {cold.Elapsed.TotalMilliseconds:F1} ms vs warm {warm.Elapsed.TotalMilliseconds:F2} ms");

        Console.WriteLine(
            $"    cold {cold.Elapsed.TotalMilliseconds:F1} ms -> warm {warm.Elapsed.TotalMilliseconds:F3} ms");

        // Prefetch: after pointing the ring at an index, the neighbours should become cached on
        // their own. This is the mechanism that makes Space and the wheel paint in one frame.
        using var prefetchPipeline = new ImagePipeline();

        // Exclude the deliberately corrupt file: prefetch correctly declines to cache anything that
        // fails to decode, so a neighbour that cannot be read would look like a prefetch failure.
        var files = FolderScanner.ScanAsync(dir, CancellationToken.None).GetAwaiter().GetResult()
            .Where(f => !Path.GetFileName(f).Contains("corrupt", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length >= 5)
        {
            const int start = 2;
            prefetchPipeline.UpdatePrefetchWindow(files, start, 1920, 1080);

            // Poll for BOTH slots. Waiting only on the forward one and then asserting the backward
            // one races the throttle: with two decode slots the behind-image is often still in
            // flight the instant the ahead-image lands.
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 10000 &&
                   !(prefetchPipeline.TryGetCached(files[start + 1], out _) &&
                     prefetchPipeline.TryGetCached(files[start - 1], out _)))
            {
                Thread.Sleep(25);
            }

            Check("prefetch warms the next image without being asked for it",
                prefetchPipeline.TryGetCached(files[start + 1], out _),
                $"still cold after {deadline.ElapsedMilliseconds} ms");

            Check("prefetch also warms the previous image",
                prefetchPipeline.TryGetCached(files[start - 1], out _),
                $"the behind-slot was not filled after {deadline.ElapsedMilliseconds} ms");

            Console.WriteLine(
                $"    prefetch filled {prefetchPipeline.CacheCount} entries " +
                $"({prefetchPipeline.CacheBytesUsed / (1024.0 * 1024):F1} MB of " +
                $"{prefetchPipeline.CacheBudgetBytes / (1024.0 * 1024):F0} MB budget)");
        }
    }

    // ------------------------------------------------------------------ timing

    private static void RunTimings(string dir)
    {
        Section("Timings (budget: 24MP full decode < 150ms)");

        foreach (var name in (string[])["large-6000x4000.jpg", "portrait-900x2400.jpg", "sample.bmp"])
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;

            var bytes = File.ReadAllBytes(path);

            // Warm the codec so the first measurement is not dominated by one-time COM setup.
            try { WicDecoder.Decode(bytes, path, 1920, 1080, CancellationToken.None); } catch { continue; }

            var fitTimes = Measure(() => WicDecoder.Decode(bytes, path, 1920, 1080, CancellationToken.None), 5);
            var fullTimes = Measure(() => WicDecoder.Decode(bytes, path, 0, 0, CancellationToken.None), 3);

            Console.WriteLine(
                $"    {name,-26} decode-to-fit {fitTimes:F1} ms   full-res {fullTimes:F1} ms   " +
                $"({bytes.Length / 1024.0:F0} KB)");
        }

        // The whole point of the preview path: first pixels on screen an order of magnitude sooner
        // than the full decode. If this margin ever collapses, the extra complexity is not earning
        // its keep and the preview stage should be dropped.
        var thumbPath = Path.Combine(dir, "with-embedded-thumb.jpg");
        if (File.Exists(thumbPath))
        {
            var tb = File.ReadAllBytes(thumbPath);
            WicDecoder.TryDecodeEmbeddedThumbnail(tb, thumbPath, CancellationToken.None);

            var thumbMs = Measure(
                () => WicDecoder.TryDecodeEmbeddedThumbnail(tb, thumbPath, CancellationToken.None), 20);
            var fullMs = Measure(
                () => WicDecoder.Decode(tb, thumbPath, 1920, 1080, CancellationToken.None), 5);

            Console.WriteLine();
            Console.WriteLine(
                $"    embedded thumbnail {thumbMs:F2} ms  vs  full decode {fullMs:F1} ms  " +
                $"({fullMs / Math.Max(thumbMs, 0.001):F0}x faster to first pixels)");
        }

        Console.WriteLine();
        Console.WriteLine("    Folder scan:");
        var sw = Stopwatch.StartNew();
        var files = FolderScanner.ScanAsync(dir, CancellationToken.None).GetAwaiter().GetResult();
        sw.Stop();
        Console.WriteLine($"    {files.Length} images enumerated + sorted in {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    private static double Measure(Action action, int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iterations;
    }

    // ------------------------------------------------------------------ helpers

    private static DecodedImage? Decode(
        string dir, string name, int maxW = 0, int maxH = 0, bool throwOnError = false)
    {
        var path = Path.Combine(dir, name);
        try
        {
            var bytes = File.ReadAllBytes(path);
            return WicDecoder.Decode(bytes, path, maxW, maxH, CancellationToken.None);
        }
        catch when (!throwOnError)
        {
            return null;
        }
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"  {title}");
        Console.WriteLine($"  {new string('-', title.Length)}");
    }

    private static void Check(string description, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"    PASS  {description}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"    FAIL  {description}" + (detail is null ? "" : $"  ({detail})"));
        }
    }
}
