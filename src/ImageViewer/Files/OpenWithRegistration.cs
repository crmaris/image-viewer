using System.IO;
using System.Runtime.InteropServices;
using ImageViewer.Imaging;
using Microsoft.Win32;

namespace ImageViewer.Files;

/// <summary>
/// Puts the application into the per-user "Open with" flyout.
/// </summary>
/// <remarks>
/// <para>
/// Windows 11 builds that short right-click menu from
/// <c>HKCU\...\Explorer\FileExts\&lt;ext&gt;\OpenWithList</c>, which is an ordinary most-recently-used
/// list with no protection on it - exactly what Windows writes for you the first time you pick an
/// application by hand. An application can therefore be a correctly registered, shell-recommended
/// handler for every image extension and still be completely absent from the menu the user actually
/// opens. That was this project's single most time-consuming bug.
/// </para>
/// <para>
/// It lives in the application rather than the installer for one reason: the list is per-user, and
/// an all-users install runs elevated, so an installer writing <c>HKCU</c> would write the
/// administrator's hive rather than the hive of the person who will use the viewer. Doing it here
/// also covers the portable build, which has no installer to do it at all.
/// </para>
/// <para>
/// <c>UserChoice</c> - the key that names the <em>default</em> handler - is deliberately never
/// touched. It carries a validated hash, forging it makes Windows discard the association outright,
/// and choosing a default is the user's call. This only makes the viewer available, never chosen.
/// </para>
/// </remarks>
public static class OpenWithRegistration
{
    private const string ExeName = "ImageViewer.exe";
    private const string ProgId = "ImageViewer.Image";

    private const string FileExtsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    /// <summary>Result of a registration pass, for the toast and the self-test.</summary>
    public readonly record struct Result(int Added, int AlreadyPresent, int Failed)
    {
        public int Total => Added + AlreadyPresent + Failed;
    }

    /// <summary>
    /// Registers the running executable and adds it to the flyout for every associatable extension.
    /// </summary>
    /// <remarks>
    /// Must not be called on the startup path: it opens several dozen registry keys and then asks
    /// Explorer to rebuild its association cache.
    /// </remarks>
    public static Result Register(string executablePath)
    {
        // Only claim the application and ProgID keys when nothing usable is already there.
        //
        // An installed copy registered these under HKLM, and HKCU\Software\Classes SHADOWS HKLM in
        // the merged view the shell reads. Writing them unconditionally would therefore repoint
        // every file association at whichever copy happened to run last - which during development
        // means a build sitting in a bin\Debug folder quietly hijacks the installed application.
        // Registering only when the existing entry is missing or points at a file that no longer
        // exists keeps the self-healing behaviour without the hijack.
        if (!HasWorkingRegistration()) RegisterApplication(executablePath);

        var added = 0;
        var already = 0;
        var failed = 0;

        foreach (var extension in SupportedFormats.AssociatableExtensions)
        {
            switch (AddToOpenWithList(extension))
            {
                case true: added++; break;
                case false: already++; break;
                case null: failed++; break;
            }
        }

        NotifyShell();
        return new Result(added, already, failed);
    }

    /// <summary>
    /// Writes the per-user application registration the flyout entry resolves through.
    /// </summary>
    /// <remarks>
    /// An <c>OpenWithList</c> entry is only the string "ImageViewer.exe"; the shell turns that into
    /// a command by looking up <c>Applications\ImageViewer.exe</c>. The installer already writes
    /// this, but repeating it here costs nothing, makes the portable build work the same way, and
    /// self-heals a registration left pointing at an executable that has since moved.
    /// </remarks>
    private static void RegisterApplication(string executablePath)
    {
        try
        {
            var command = $"\"{executablePath}\" \"%1\"";

            using (var app = Registry.CurrentUser.CreateSubKey(
                       $@"Software\Classes\Applications\{ExeName}"))
            {
                app?.SetValue("FriendlyAppName", "Image Viewer");
            }

            using (var open = Registry.CurrentUser.CreateSubKey(
                       $@"Software\Classes\Applications\{ExeName}\shell\open\command"))
            {
                open?.SetValue(string.Empty, command);
            }

            // The shell reads SupportedTypes when building the full Open With list.
            using (var types = Registry.CurrentUser.CreateSubKey(
                       $@"Software\Classes\Applications\{ExeName}\SupportedTypes"))
            {
                if (types is not null)
                {
                    foreach (var extension in SupportedFormats.AssociatableExtensions)
                        types.SetValue(extension, string.Empty);
                }
            }

            // The ProgID itself, so "Choose another app" has something to offer per extension.
            using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                progId?.SetValue(string.Empty, "Image");
            }

            using (var icon = Registry.CurrentUser.CreateSubKey(
                       $@"Software\Classes\{ProgId}\DefaultIcon"))
            {
                icon?.SetValue(string.Empty, $"{executablePath},0");
            }

            using (var progIdCommand = Registry.CurrentUser.CreateSubKey(
                       $@"Software\Classes\{ProgId}\shell\open\command"))
            {
                progIdCommand?.SetValue(string.Empty, command);
            }
        }
        catch
        {
            // A locked-down profile can refuse these writes. The viewer still opens files fine.
        }
    }

    /// <summary>
    /// Adds the executable to one extension's flyout list.
    /// </summary>
    /// <returns>
    /// True if an entry was added, false if it was already listed, null if the write failed.
    /// </returns>
    /// <remarks>
    /// Appended to the MRU rather than promoted to the front. Being present is what fixes the bug;
    /// jumping ahead of an application the user has actually been choosing - a RAW editor for
    /// <c>.cr2</c>, say - would be presumptuous. On the very common case of an extension with no
    /// list at all, appending still makes the viewer the first and only entry.
    /// </remarks>
    private static bool? AddToOpenWithList(string extension)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{FileExtsKey}\{extension}\OpenWithList");
            if (key is null) return null;

            var slots = key.GetValueNames()
                .Where(n => !string.Equals(n, "MRUList", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(n => n, n => key.GetValue(n) as string ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);

            var mru = key.GetValue("MRUList") as string ?? string.Empty;

            var existing = slots.FirstOrDefault(
                s => string.Equals(s.Value, ExeName, StringComparison.OrdinalIgnoreCase));

            if (existing.Key is not null)
            {
                // Already listed, but a truncated MRUList would hide it. Make sure it is referenced.
                if (!mru.Contains(existing.Key, StringComparison.Ordinal))
                    key.SetValue("MRUList", mru + existing.Key);

                return false;
            }

            // Slots are single lowercase letters, allocated the way the shell allocates them.
            var letter = "abcdefghijklmnopqrstuvwxyz"
                .Select(c => c.ToString())
                .FirstOrDefault(c => !slots.ContainsKey(c));

            // Twenty-six handlers already listed. Vanishingly unlikely, and not worth evicting one.
            if (letter is null) return null;

            key.SetValue(letter, ExeName);
            key.SetValue("MRUList", mru + letter);
            return true;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tells Explorer its cached association data is stale.
    /// </summary>
    /// <remarks>
    /// Without this the registry is correct and the menu still shows the old list, which reads as
    /// the registration having silently failed.
    /// </remarks>
    private static void NotifyShell()
    {
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Cosmetic only; the entries are written either way.
        }
    }

    /// <summary>
    /// True when the shell already resolves ImageViewer.exe to an executable that still exists.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="Registry.ClassesRoot"/> because that is the merged HKLM+HKCU view
    /// the shell itself uses - checking either hive alone would miss a perfectly good registration
    /// written by the other.
    /// </remarks>
    public static bool HasWorkingRegistration()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(
                $@"Applications\{ExeName}\shell\open\command");

            if (key?.GetValue(string.Empty) is not string command || command.Length == 0)
                return false;

            var executable = ExtractExecutable(command);
            return executable.Length > 0 && File.Exists(executable);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Pulls the executable path out of a shell open command.</summary>
    private static string ExtractExecutable(string command)
    {
        var trimmed = command.TrimStart();

        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : string.Empty;
        }

        // Unquoted, so it ends at the first space - which also means a path containing one cannot
        // be recovered. Nothing this application writes is unquoted; this is for other people's.
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    /// <summary>True if the executable is already listed for the given extension.</summary>
    public static bool IsListed(string extension)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{FileExtsKey}\{extension}\OpenWithList");
            if (key is null) return false;

            return key.GetValueNames()
                .Where(n => !string.Equals(n, "MRUList", StringComparison.OrdinalIgnoreCase))
                .Any(n => string.Equals(key.GetValue(n) as string, ExeName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Full path of the running executable, or null if it cannot be determined.</summary>
    public static string? ExecutablePath()
    {
        try
        {
            var path = Environment.ProcessPath;
            return path is not null && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
