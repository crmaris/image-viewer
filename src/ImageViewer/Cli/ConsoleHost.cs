using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ImageViewer.Cli;

/// <summary>
/// Gives this GUI executable a working console when it is invoked from a shell.
/// </summary>
/// <remarks>
/// <para>
/// The application is built as a <c>WinExe</c>, which is what stops a console window flashing up
/// every time someone double-clicks an image. The cost is that the process starts with no console
/// attached at all, so <see cref="Console.WriteLine"/> silently writes nowhere. Attaching to the
/// parent process's console fixes that without ever creating a window of our own.
/// </para>
/// <para>
/// Rebinding the streams afterwards is not optional. .NET resolves <see cref="Console.Out"/> lazily
/// but caches it forever, and in a windowed process the first resolution yields a null writer; the
/// attach would then succeed and output would still vanish.
/// </para>
/// <para>
/// Redirection is handled by the same path. When stdout is a file or a pipe the standard handle is
/// already valid and <c>AttachConsole</c> simply fails, which is why its result is ignored rather
/// than treated as an error - piping to a file works even with no console anywhere in sight.
/// </para>
/// <para>
/// One thing this cannot fix: because the process is a <c>WinExe</c>, cmd.exe and PowerShell do not
/// wait for it, so the shell prompt returns before the output is printed. That is cosmetic in
/// interactive use and invisible when redirecting, which is the case that matters for scripting.
/// </para>
/// </remarks>
internal static class ConsoleHost
{
    private const int AttachParentProcess = -1;

    private static bool _prepared;

    /// <summary>Attaches to the calling shell's console and points the console streams at it.</summary>
    internal static void Prepare()
    {
        if (_prepared) return;
        _prepared = true;

        // Failure is normal and ignored: it means either there is no parent console, or output is
        // already redirected somewhere that does not need one.
        AttachConsole(AttachParentProcess);

        Rebind(Console.OpenStandardOutput, Console.SetOut);
        Rebind(Console.OpenStandardError, Console.SetError);
    }

    private static void Rebind(Func<Stream> open, Action<TextWriter> set)
    {
        try
        {
            var stream = open();
            if (stream == Stream.Null) return;

            // AutoFlush because the process may exit at any point after a command finishes, and a
            // buffered writer that never gets disposed loses whatever was still in it.
            set(new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            });
        }
        catch
        {
            // Leave the default writer in place. Losing output is better than failing to run.
        }
    }

    /// <summary>
    /// Writes the trailing newline an interactive shell needs to line its prompt up again.
    /// </summary>
    /// <remarks>
    /// Skipped when output is redirected, where it would be a stray blank line in the captured text
    /// rather than a cosmetic fix.
    /// </remarks>
    internal static void Finish()
    {
        try
        {
            if (!Console.IsOutputRedirected) Console.Out.Write(Environment.NewLine);
            Console.Out.Flush();
            Console.Error.Flush();
        }
        catch
        {
            // Nothing useful to do while shutting down.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
