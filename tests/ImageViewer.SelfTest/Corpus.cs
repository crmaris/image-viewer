using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageMagick.Drawing;   // Drawables moved here in Magick.NET 14

namespace ImageViewer.SelfTest;

/// <summary>
/// Generates the entire test corpus from nothing.
/// </summary>
/// <remarks>
/// <para>
/// Every file the suite needs is produced here, so a clean clone or a CI runner can go from
/// checkout to a full test run with no external assets and nothing committed to the repository.
/// An earlier version only produced the exotic formats and relied on images created by hand, which
/// would have failed the moment it ran anywhere but the machine it was written on.
/// </para>
/// <para>
/// The point is to test files the viewer did not create, so formats come from Magick.NET and the
/// EXIF cases from WPF's own encoder rather than being round-tripped through the viewer's code.
/// </para>
/// </remarks>
public static class Corpus
{
    public static int Generate(string dir)
    {
        Directory.CreateDirectory(dir);

        var written = 0;
        written += GenerateEveryday(dir);
        written += GenerateExifCases(dir);
        written += GenerateExotic(dir);
        written += GenerateEdgeCases(dir);

        return written;
    }

    /// <summary>Draws a recognisable test image: colour bands plus an off-centre marker.</summary>
    /// <remarks>
    /// The asymmetry is deliberate. A centred or symmetric pattern would hide exactly the bugs this
    /// corpus exists to catch - a mishandled rotation, a flip applied to the wrong axis, or a
    /// channel order swapped between BGRA and RGBA.
    /// </remarks>
    private static MagickImage Draw(uint width, uint height, string label, MagickColor background)
    {
        var image = new MagickImage(background, width, height);

        var w = (double)width;
        var h = (double)height;

        new Drawables()
            .FillColor(MagickColors.OrangeRed).Rectangle(0, 0, w * 0.25, h * 0.25)      // top-left
            .FillColor(MagickColors.White).Rectangle(w * 0.3, h * 0.42, w * 0.7, h * 0.58)
            .FillColor(MagickColors.LimeGreen).Ellipse(w * 0.78, h * 0.8, w * 0.14, h * 0.12, 0, 360)
            .FillColor(MagickColors.White)
            .FontPointSize(Math.Max(11, h / 12))
            .Text(w * 0.06, h * 0.22, label)
            .Draw(image);

        return image;
    }

    /// <summary>The formats WIC handles natively, plus the natural-sort probe.</summary>
    private static int GenerateEveryday(string dir)
    {
        var written = 0;

        // Natural sort: these must page 1, 2, 3, 10, 20 - not the lexicographic 1, 10, 2, 20, 3.
        foreach (var n in (int[])[1, 2, 3, 10, 20])
        {
            using var image = Draw(800, 600, $"img{n}", MagickColors.DarkSlateGray);
            image.Write(Path.Combine(dir, $"img{n}.png"), MagickFormat.Png);
            written++;
        }

        foreach (var (ext, format) in ((string, MagickFormat)[])
                 [("jpg", MagickFormat.Jpeg), ("bmp", MagickFormat.Bmp),
                  ("gif", MagickFormat.Gif), ("tif", MagickFormat.Tiff)])
        {
            using var image = Draw(1024, 768, ext.ToUpperInvariant(), MagickColors.MidnightBlue);
            image.Write(Path.Combine(dir, $"sample.{ext}"), format);
            written++;
        }

        // Large enough to make decode-to-fit measurable and to exercise the downscale path.
        using (var big = Draw(6000, 4000, "6000x4000", MagickColors.Black))
        {
            big.Write(Path.Combine(dir, "large-6000x4000.jpg"), MagickFormat.Jpeg);
            written++;
        }

        // Portrait: the fit calculation must constrain on the other axis.
        using (var portrait = Draw(900, 2400, "portrait", MagickColors.DarkGreen))
        {
            portrait.Write(Path.Combine(dir, "portrait-900x2400.jpg"), MagickFormat.Jpeg);
            written++;
        }

        // Must NOT be blown up to fill the window under Fit.
        using (var tiny = Draw(64, 64, "64", MagickColors.Purple))
        {
            tiny.Write(Path.Combine(dir, "tiny-64.png"), MagickFormat.Png);
            written++;
        }

        // Non-ASCII name: confirms dropping InvariantGlobalization was the right call.
        using (var greek = Draw(800, 600, "ellinika", MagickColors.Teal))
        {
            greek.Write(Path.Combine(dir, "δοκιμή-εικόνα.png"), MagickFormat.Png);
            written++;
        }

        return written;
    }

    /// <summary>
    /// JPEGs carrying EXIF orientation tags and an embedded thumbnail.
    /// </summary>
    /// <remarks>
    /// Written with WPF's encoder rather than Magick.NET on purpose: these files exist to prove the
    /// viewer reads metadata the way Windows itself writes it.
    /// </remarks>
    private static int GenerateExifCases(string dir)
    {
        var written = 0;

        // Landscape source; orientation 6 means a correct viewer displays it as portrait.
        var landscape = Path.Combine(dir, "_exif-source.jpg");
        using (var image = Draw(1200, 800, "EXIF-6 landscape source", MagickColors.Maroon))
        {
            image.Write(landscape, MagickFormat.Jpeg);
        }

        WriteWithOrientation(landscape, Path.Combine(dir, "exif-orientation-6.jpg"), 6);
        written++;

        // The other quarter turn, from a differently-shaped source.
        WriteWithOrientation(
            Path.Combine(dir, "sample.jpg"), Path.Combine(dir, "exif-orientation-8.jpg"), 8);
        written++;

        File.Delete(landscape);

        // A JPEG with an embedded preview, for the instant-first-paint path. Magick.NET does not
        // write one, so this goes through WPF as well.
        WriteWithThumbnail(
            Path.Combine(dir, "large-6000x4000.jpg"), Path.Combine(dir, "with-embedded-thumb.jpg"));
        written++;

        return written;
    }

    private static void WriteWithOrientation(string sourcePath, string targetPath, ushort orientation)
    {
        using var source = File.OpenRead(sourcePath);
        var decoder = BitmapDecoder.Create(
            source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);

        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0], null, metadata, null));

        using var output = File.Create(targetPath);
        encoder.Save(output);
    }

    private static void WriteWithThumbnail(string sourcePath, string targetPath)
    {
        using var source = File.OpenRead(sourcePath);
        var decoder = BitmapDecoder.Create(
            source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];

        // Roughly the size a camera embeds.
        var factor = 160.0 / frame.PixelWidth;
        var scale = new System.Windows.Media.ScaleTransform(factor, factor);
        scale.Freeze();

        var thumbnail = new TransformedBitmap(frame, scale);
        thumbnail.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(frame, thumbnail, null, null));

        using var output = File.Create(targetPath);
        encoder.Save(output);
    }

    /// <summary>Formats that force the decoder chain past WIC into its fallback tiers.</summary>
    private static int GenerateExotic(string dir)
    {
        var written = 0;

        using (var source = Draw(640, 480, "exotic", MagickColors.MidnightBlue))
        {
            foreach (var (ext, format) in ((string, MagickFormat)[])
                     [
                         ("psd", MagickFormat.Psd),
                         ("tga", MagickFormat.Tga),
                         ("pcx", MagickFormat.Pcx),
                         ("ppm", MagickFormat.Ppm),
                         ("pgm", MagickFormat.Pgm),
                         ("exr", MagickFormat.Exr),
                         ("hdr", MagickFormat.Hdr),
                         ("jp2", MagickFormat.Jp2),
                         ("webp", MagickFormat.WebP),
                         ("qoi", MagickFormat.Qoi),
                     ])
            {
                var path = Path.Combine(dir, $"exotic.{ext}");
                try
                {
                    using var copy = source.Clone();
                    copy.Format = format;
                    copy.Write(path);
                    written++;
                }
                catch (Exception ex)
                {
                    // Some formats need delegates that may not be present in every build.
                    Console.WriteLine($"    skipped .{ext}: {ex.Message}");
                }
            }
        }

        // SVG is text, so hand-write it: that way the file really is a vector and the Svg.Skia tier
        // is genuinely exercised rather than being handed a rasterised impostor.
        File.WriteAllText(Path.Combine(dir, "vector.svg"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 300" width="400" height="300">
              <rect width="400" height="300" fill="#2E7BFF"/>
              <circle cx="300" cy="80" r="45" fill="#FFFFFF"/>
              <polygon points="40,260 160,90 280,260" fill="#FFFFFF"/>
              <text x="20" y="40" font-family="sans-serif" font-size="28" fill="#FFFFFF">vector</text>
            </svg>
            """);
        written++;

        // Gzipped SVG: no distinguishing header, so this is what the extension fallback is for.
        var svgz = Path.Combine(dir, "vector.svgz");
        using (var input = File.OpenRead(Path.Combine(dir, "vector.svg")))
        using (var output = File.Create(svgz))
        using (var gz = new System.IO.Compression.GZipStream(
                   output, System.IO.Compression.CompressionLevel.Optimal))
        {
            input.CopyTo(gz);
        }
        written++;

        // Animated GIF, for the frame-compositing path.
        using (var frames = new MagickImageCollection())
        {
            foreach (var colour in (MagickColor[])
                     [MagickColors.Red, MagickColors.Green, MagickColors.Blue, MagickColors.Yellow])
            {
                frames.Add(new MagickImage(colour, 200, 200) { AnimationDelay = 10 });
            }
            frames.Write(Path.Combine(dir, "animated.gif"), MagickFormat.Gif);
            written++;
        }

        return written;
    }

    /// <summary>Files designed to be wrong in specific ways.</summary>
    private static int GenerateEdgeCases(string dir)
    {
        // Real PNG bytes wearing a .jpg extension: the viewer must identify it by content.
        File.Copy(
            Path.Combine(dir, "img1.png"),
            Path.Combine(dir, "mislabelled-actually-png.jpg"),
            overwrite: true);

        // A JPEG header followed by nonsense. Must produce a reported error, never a crash.
        File.WriteAllBytes(Path.Combine(dir, "corrupt.jpg"),
            [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 2, 3, 4, 5]);

        return 2;
    }
}
