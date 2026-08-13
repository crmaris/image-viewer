using System.IO;
using ImageMagick;
using ImageMagick.Drawing;   // Drawables moved here in Magick.NET 14

namespace ImageViewer.SelfTest;

/// <summary>
/// Generates test files in the exotic formats, so the fallback decoder tiers are actually exercised.
/// </summary>
/// <remarks>
/// Uses Magick.NET to write formats nothing else on the machine can produce. This deliberately
/// lives in the test project, not the application: the point is to prove the viewer can read files
/// it did not create.
/// </remarks>
public static class Corpus
{
    public static int Generate(string dir)
    {
        Directory.CreateDirectory(dir);
        var written = 0;

        // A recognisable source image: coloured bands plus an off-centre marker, so an orientation
        // or channel-order bug is visible rather than subtle.
        using (var source = new MagickImage(MagickColors.MidnightBlue, 640, 480))
        {
            var drawables = new Drawables()
                .FillColor(MagickColors.OrangeRed).Rectangle(0, 0, 160, 120)
                .FillColor(MagickColors.White).Rectangle(200, 200, 440, 280)
                .FillColor(MagickColors.LimeGreen).Ellipse(500, 380, 90, 60, 0, 360);
            drawables.Draw(source);

            // One file per format the WIC and ImageSharp tiers cannot handle, which is precisely
            // what forces the chain down to Magick.NET.
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

        // SVG is text, so hand-write it rather than round-tripping through a rasteriser - that way
        // the file really is a vector and the Svg.Skia tier is genuinely exercised.
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

        // Gzipped SVG: same content, the .svgz path.
        var svgz = Path.Combine(dir, "vector.svgz");
        using (var input = File.OpenRead(Path.Combine(dir, "vector.svg")))
        using (var output = File.Create(svgz))
        using (var gz = new System.IO.Compression.GZipStream(
                   output, System.IO.Compression.CompressionLevel.Optimal))
        {
            input.CopyTo(gz);
        }
        written++;

        // Animated GIF, for the frame-timing path.
        using (var frames = new MagickImageCollection())
        {
            foreach (var colour in (MagickColor[])
                     [MagickColors.Red, MagickColors.Green, MagickColors.Blue, MagickColors.Yellow])
            {
                var frame = new MagickImage(colour, 200, 200) { AnimationDelay = 10 };
                frames.Add(frame);
            }
            frames.Write(Path.Combine(dir, "animated.gif"), MagickFormat.Gif);
            written++;
        }

        return written;
    }
}
