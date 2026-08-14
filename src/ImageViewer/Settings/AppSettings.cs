using System.Globalization;
using System.IO;
using System.Text;

namespace ImageViewer.Settings;

/// <summary>
/// The handful of things worth remembering between launches.
/// </summary>
/// <remarks>
/// <para>
/// Stored as flat <c>key=value</c> text rather than JSON. The file is a dozen short lines, and
/// <see cref="System.Text.Json.JsonSerializer"/>'s first call builds reflection-based metadata for
/// the type - tens of milliseconds against a cold-start budget where an empty WPF window already
/// costs most of the second. Parsing this by hand is a few microseconds, adds no assembly to the
/// startup path, and has the side benefit of being obvious to edit by hand.
/// </para>
/// <para>
/// Every number is read and written with <see cref="CultureInfo.InvariantCulture"/>. This is not
/// decoration: <c>InvariantGlobalization</c> is deliberately switched off in this project (Greek
/// filenames have to sort and display correctly), so on a machine whose locale uses a decimal comma
/// a round-trip through the current culture would write <c>4,5</c> and then fail to read it back.
/// </para>
/// <para>
/// A missing, unreadable or partially corrupt file is never an error - anything that cannot be
/// parsed simply keeps its default. Settings are a convenience; refusing to start over them, or
/// throwing away every value because one line is malformed, would be far worse than losing them.
/// </para>
/// </remarks>
public sealed class AppSettings
{
    /// <summary>Bumped only if the format ever changes incompatibly; unknown keys are ignored.</summary>
    private const int CurrentVersion = 1;

    // ---- window placement ---------------------------------------------------------------------

    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public bool Fullscreen { get; set; }

    // ---- view preferences ---------------------------------------------------------------------

    public double SlideshowSeconds { get; set; } = 4;
    public bool InfoVisible { get; set; }
    public bool FilmstripVisible { get; set; }

    // There is deliberately no "colour management" switch here. Measurement showed WIC already
    // applies embedded ICC profiles on the decode path, so a toggle by that name would only be able
    // to control the small palettised-image correction in WicDecoder - a setting whose name
    // promised far more than it did. See the colour-management section of CLAUDE.md.

    // ---- first-run bookkeeping ------------------------------------------------------------------

    /// <summary>
    /// Executable path the "Open with" registration was last written for; empty if never.
    /// </summary>
    /// <remarks>
    /// The path rather than a plain flag, for two reasons. It keeps the registration to once per
    /// install - re-adding an entry the user deliberately removed would be the viewer arguing with
    /// them - while still re-running if the application moves, which it does when a portable copy
    /// is relocated or an install switches between per-user and all-users. A stale registration
    /// points the shell at an executable that is no longer there.
    /// </remarks>
    public string OpenWithRegisteredFor { get; set; } = string.Empty;

    // ---- storage ---------------------------------------------------------------------------------

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageViewer");

    public static string FilePath => Path.Combine(Directory, "settings.txt");

    /// <summary>Reads the user's settings file.</summary>
    public static AppSettings Load() => Load(FilePath);

    /// <summary>
    /// Reads a settings file from an explicit path, falling back to defaults for anything missing
    /// or broken.
    /// </summary>
    /// <remarks>
    /// The path is a parameter so the self-test can exercise this parser against scratch files.
    /// <see cref="Environment.GetFolderPath"/> asks the shell for the real roaming folder and
    /// ignores the APPDATA environment variable, so redirecting it is not an option - without this
    /// overload a test would either read the developer's own settings or, worse, overwrite them.
    /// </remarks>
    public static AppSettings Load(string path)
    {
        var settings = new AppSettings();

        try
        {
            if (!File.Exists(path)) return settings;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.AsSpan().Trim();
                if (line.IsEmpty || line[0] == '#') continue;

                var split = line.IndexOf('=');
                if (split <= 0) continue;

                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();

                settings.Apply(key.ToString(), value.ToString());
            }
        }
        catch
        {
            // An unreadable settings file must not stop the viewer opening. Defaults are fine.
        }

        return settings;
    }

    private void Apply(string key, string value)
    {
        switch (key)
        {
            case "windowLeft": WindowLeft = ReadDouble(value, WindowLeft); break;
            case "windowTop": WindowTop = ReadDouble(value, WindowTop); break;
            case "windowWidth": WindowWidth = ReadDouble(value, WindowWidth); break;
            case "windowHeight": WindowHeight = ReadDouble(value, WindowHeight); break;
            case "windowMaximized": WindowMaximized = ReadBool(value, WindowMaximized); break;
            case "fullscreen": Fullscreen = ReadBool(value, Fullscreen); break;
            case "slideshowSeconds": SlideshowSeconds = ReadDouble(value, SlideshowSeconds); break;
            case "infoVisible": InfoVisible = ReadBool(value, InfoVisible); break;
            case "filmstripVisible": FilmstripVisible = ReadBool(value, FilmstripVisible); break;
            case "openWithRegisteredFor": OpenWithRegisteredFor = value; break;

            // "version" and anything written by a future build are ignored on purpose, so an older
            // binary reading a newer file degrades to defaults rather than failing.
        }
    }

    private static double ReadDouble(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool ReadBool(string text, bool fallback) =>
        bool.TryParse(text, out var value) ? value : fallback;

    /// <summary>
    /// Writes the settings file.
    /// </summary>
    /// <remarks>
    /// Via a temporary file and a move, so a crash or a full disk part-way through leaves the
    /// previous settings intact rather than a truncated file the next launch has to discard.
    /// Silent on failure: a read-only profile is a reason to lose preferences, not to show an error
    /// as the window is closing.
    /// </remarks>
    public void Save() => Save(FilePath);

    /// <inheritdoc cref="Save()"/>
    public void Save(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);

            var text = new StringBuilder()
                .AppendLine("# Image Viewer settings. Delete this file to return to the defaults.")
                .Append("version=").Append(CurrentVersion).AppendLine()
                .Append("windowLeft=").AppendLine(Format(WindowLeft))
                .Append("windowTop=").AppendLine(Format(WindowTop))
                .Append("windowWidth=").AppendLine(Format(WindowWidth))
                .Append("windowHeight=").AppendLine(Format(WindowHeight))
                .Append("windowMaximized=").AppendLine(Format(WindowMaximized))
                .Append("fullscreen=").AppendLine(Format(Fullscreen))
                .Append("slideshowSeconds=").AppendLine(Format(SlideshowSeconds))
                .Append("infoVisible=").AppendLine(Format(InfoVisible))
                .Append("filmstripVisible=").AppendLine(Format(FilmstripVisible))
                .Append("openWithRegisteredFor=").AppendLine(OpenWithRegisteredFor)
                .ToString();

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, text);

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // Losing preferences is a minor inconvenience; interrupting shutdown is not acceptable.
        }
    }

    private static string Format(double value) =>
        double.IsNaN(value) ? "auto" : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Format(bool value) => value ? "true" : "false";
}
