using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ImageViewer.Files;

/// <summary>
/// Windows shell operations: Recycle Bin, Explorer integration and renaming.
/// </summary>
public static partial class ShellOps
{
    /// <summary>
    /// Sends a file to the Recycle Bin.
    /// </summary>
    /// <remarks>
    /// Uses the shell's own file operation rather than <see cref="File.Delete"/>, which destroys
    /// the file outright with no way back. Culling a folder of test shots is exactly the situation
    /// where a mistaken keypress needs to be recoverable.
    /// </remarks>
    public static bool MoveToRecycleBin(string path)
    {
        if (!File.Exists(path)) return false;

        // The path list is double-null terminated.
        var operation = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };

        var result = SHFileOperationW(ref operation);
        return result == 0 && !operation.fAnyOperationsAborted;
    }

    /// <summary>Deletes permanently, bypassing the Recycle Bin.</summary>
    public static bool DeletePermanently(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void ShowInExplorer(string path)
    {
        try
        {
            // /select, needs the path quoted but the switch outside the quotes.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Explorer being unavailable is not worth interrupting the user over.
        }
    }

    /// <summary>
    /// Renames a file within its folder.
    /// </summary>
    /// <returns>The new full path.</returns>
    /// <exception cref="IOException">The target name already exists.</exception>
    public static string Rename(string path, string newFileName)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new IOException("The file has no containing folder.");

        // Guard against a name that would move the file somewhere else entirely.
        if (newFileName.Contains(Path.DirectorySeparatorChar) ||
            newFileName.Contains(Path.AltDirectorySeparatorChar) ||
            newFileName.Contains(':'))
        {
            throw new IOException("A file name cannot contain a path.");
        }

        if (newFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("That name contains characters a file name cannot use.");

        var target = Path.Combine(directory, newFileName);

        if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) return path;

        if (File.Exists(target))
            throw new IOException($"'{newFileName}' already exists in this folder.");

        File.Move(path, target);
        return target;
    }

    // ------------------------------------------------------------ interop

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;      // this flag is what routes to the Recycle Bin
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    // DllImport rather than LibraryImport: the struct carries string fields that need the
    // marshaller, which the source generator does not support.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW fileOp);
}
