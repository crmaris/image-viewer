using System.Globalization;

namespace ImageViewer;

/// <summary>
/// What a normal, window-opening launch was asked for.
/// </summary>
/// <remarks>
/// Parsed before any WPF type is referenced, so it has to stay trivial: a loop over the arguments
/// and nothing else. Anything heavier here would land squarely on the cold-start path this whole
/// application is built around.
/// </remarks>
public sealed class LaunchOptions
{
    public string[] Paths { get; private init; } = [];

    /// <summary>Open full screen, as though F11 had been pressed.</summary>
    public bool Fullscreen { get; private init; }

    /// <summary>Start a slideshow once the folder has been scanned.</summary>
    public bool Slideshow { get; private init; }

    /// <summary>Seconds per slide, or zero to use whatever the last session left.</summary>
    public double SlideshowSeconds { get; private init; }

    public static LaunchOptions Parse(string[] args)
    {
        if (args.Length == 0) return new LaunchOptions();

        List<string> paths = new(args.Length);
        var fullscreen = false;
        var slideshow = false;
        var seconds = 0d;
        var literal = false;

        foreach (var argument in args)
        {
            if (string.IsNullOrWhiteSpace(argument)) continue;

            if (literal)
            {
                paths.Add(argument.Trim('"'));
                continue;
            }

            // Everything after "--" is a path, which is how a file whose name begins with a dash,
            // or one called "info", can be opened at all.
            if (argument == "--")
            {
                literal = true;
                continue;
            }

            if (argument.StartsWith('-'))
            {
                var name = argument.TrimStart('-');
                var value = string.Empty;

                var equals = name.IndexOf('=');
                if (equals > 0)
                {
                    value = name[(equals + 1)..];
                    name = name[..equals];
                }

                switch (name.ToLowerInvariant())
                {
                    case "fullscreen" or "f":
                        fullscreen = true;
                        break;

                    case "slideshow" or "s":
                        slideshow = true;
                        // Invariant culture: on a decimal-comma locale "4.5" must still parse,
                        // because it is what a script or a shortcut would have been written with.
                        if (double.TryParse(
                                value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        {
                            seconds = parsed;
                        }
                        break;

                    // Anything else is ignored rather than rejected. Explorer and third-party
                    // launchers pass switches of their own, and refusing to open an image over one
                    // of them would be a poor trade.
                }

                continue;
            }

            // A bare "/something" that is not a short switch is a path on this platform only in
            // odd cases, but the original parser allowed it and shortcuts in the wild rely on it.
            if (argument.StartsWith('/') && argument.Length <= 3) continue;

            paths.Add(argument.Trim('"'));
        }

        return new LaunchOptions
        {
            Paths = [.. paths],
            Fullscreen = fullscreen,
            Slideshow = slideshow,
            SlideshowSeconds = seconds,
        };
    }
}
