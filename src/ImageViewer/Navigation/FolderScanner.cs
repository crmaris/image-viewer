using System.IO;
using System.Runtime.InteropServices;
using ImageViewer.Imaging;

namespace ImageViewer.Navigation;

/// <summary>
/// Builds the ordered list of images in a folder.
/// </summary>
/// <remarks>
/// Scanning is deliberately kept off the startup path. Opening one file out of a 10,000-image
/// directory must not wait for the directory to be enumerated, so the caller decodes the requested
/// image first and calls <see cref="ScanAsync"/> afterwards on a background thread.
/// </remarks>
public sealed partial class FolderScanner
{
    /// <summary>
    /// Enumerates the supported images in <paramref name="folder"/>, sorted the way Explorer sorts.
    /// </summary>
    public static Task<string[]> ScanAsync(string folder, CancellationToken ct) =>
        Task.Run(() => Scan(folder, ct), ct);

    private static string[] Scan(string folder, CancellationToken ct)
    {
        List<string> found = [];

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                // Hidden files are skipped, but system-flagged ones are not: network shares and
                // some cameras mark ordinary images as system.
                AttributesToSkip = FileAttributes.Hidden,
            };

            foreach (var path in Directory.EnumerateFiles(folder, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (SupportedFormats.IsSupported(path)) found.Add(path);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // An unreadable or vanished folder just means single-image mode; not worth failing over.
            return [];
        }

        var result = found.ToArray();
        Array.Sort(result, CompareNatural);
        return result;
    }

    /// <summary>
    /// Orders names the way Windows Explorer does, so "img2" sorts before "img10".
    /// </summary>
    /// <remarks>
    /// Plain ordinal sorting puts "img10" before "img2", which makes paging through a numbered
    /// series of test shots jump around unpredictably. StrCmpLogicalW is the exact comparison
    /// Explorer itself uses, so the viewer's order always matches what the user sees in the folder.
    /// </remarks>
    public static int CompareNatural(string a, string b)
    {
        var result = StrCmpLogicalW(a, b);
        // Fall back to an ordinal tiebreak so the sort is total and stable.
        return result != 0 ? result : string.CompareOrdinal(a, b);
    }

    [LibraryImport("shlwapi.dll", EntryPoint = "StrCmpLogicalW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string x, string y);
}
