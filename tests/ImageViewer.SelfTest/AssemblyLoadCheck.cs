using System.IO;
using ImageViewer.Imaging;

namespace ImageViewer.SelfTest;

/// <summary>
/// Proves the heavy decoder tiers stay unloaded while viewing ordinary images.
/// </summary>
/// <remarks>
/// <para>
/// This is the check that keeps the "opens everything" and "starts fast" requirements from being
/// in conflict. Referencing ImageSharp, SkiaSharp and Magick.NET adds roughly 55 MB of libraries;
/// the design depends on the CLR never loading them unless an exotic file is actually opened. That
/// is easy to break by accident - one field of a library type, one method signature, and the
/// assembly loads at startup for every JPEG.
/// </para>
/// <para>
/// It must run in its own process. The main check suite deliberately exercises every tier, so by
/// the time it finishes, all three are loaded and the question cannot be asked.
/// </para>
/// </remarks>
public static class AssemblyLoadCheck
{
    /// <summary>Assemblies that must not appear from viewing everyday images.</summary>
    private static readonly string[] HeavyAssemblies =
        ["SixLabors.ImageSharp", "SkiaSharp", "Svg.Skia", "Magick.NET"];

    public static int Run(string dir)
    {
        Console.WriteLine("Assembly-load check (fresh process)");
        Console.WriteLine("-----------------------------------");

        var before = LoadedHeavyAssemblies();
        Console.WriteLine($"  before any decode : {Describe(before)}");

        // Everything WIC handles natively: exactly what a normal session opens.
        var everyday = new[]
        {
            "img1.png", "img2.png", "sample.jpg", "sample.bmp", "sample.gif", "sample.tif",
            "large-6000x4000.jpg", "portrait-900x2400.jpg", "exif-orientation-6.jpg",
            "with-embedded-thumb.jpg", "tiny-64.png", "δοκιμή-εικόνα.png",
        };

        var decoded = 0;
        foreach (var name in everyday)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;

            var bytes = File.ReadAllBytes(path);
            // Go through the real chain, not WicDecoder directly - the chain is what has to avoid
            // touching the heavy tiers.
            DecoderChain.Decode(bytes, path, 1920, 1080, CancellationToken.None);
            decoded++;
        }

        // Repeat, to be sure nothing loads on a later pass through a warmed-up code path.
        for (var i = 0; i < 3; i++)
        {
            foreach (var name in everyday)
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                DecoderChain.Decode(File.ReadAllBytes(path), path, 1920, 1080, CancellationToken.None);
            }
        }

        var after = LoadedHeavyAssemblies();
        Console.WriteLine($"  after {decoded * 4} decodes : {Describe(after)}");

        if (after.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  PASS  the heavy decoder tiers never loaded");
            Console.WriteLine("        (so their ~55 MB costs nothing on the normal viewing path)");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"  FAIL  these loaded during ordinary viewing: {string.Join(", ", after)}");
        Console.WriteLine("        Something references a fallback decoder's types outside a method body -");
        Console.WriteLine("        check for fields, constructors or signatures mentioning them.");
        return 1;
    }

    private static List<string> LoadedHeavyAssemblies() =>
        [.. AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty)
            .Where(n => HeavyAssemblies.Any(h => n.StartsWith(h, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .Order()];

    private static string Describe(List<string> loaded) =>
        loaded.Count == 0 ? "none loaded" : string.Join(", ", loaded);
}
