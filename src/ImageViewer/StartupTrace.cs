using System.Diagnostics;
using System.IO;
using System.Text;

namespace ImageViewer;

/// <summary>
/// Optional startup instrumentation, enabled by setting IMAGEVIEWER_STARTUP_LOG to a file path.
/// </summary>
/// <remarks>
/// Records stage timings measured from <see cref="Process.StartTime"/>, so the numbers include
/// runtime initialisation and JIT rather than flattering themselves by starting the clock at Main.
/// Splitting startup into stages is what makes a regression actionable: a slow "main" points at
/// runtime and assembly loading, a slow "shown" at WPF initialisation, and a slow "rendered" at
/// rendering or the first decode.
/// </remarks>
public static class StartupTrace
{
    private static readonly string? LogPath =
        Environment.GetEnvironmentVariable("IMAGEVIEWER_STARTUP_LOG");

    /// <summary>True when tracing is on; checked before doing any work.</summary>
    public static bool Enabled => !string.IsNullOrWhiteSpace(LogPath);

    private static readonly StringBuilder Stages = new();
    private static DateTime _processStart;

    public static void Begin()
    {
        if (!Enabled) return;

        try
        {
            using var process = Process.GetCurrentProcess();
            _processStart = process.StartTime;
        }
        catch
        {
            _processStart = DateTime.Now;
        }
    }

    /// <summary>Records the time from process start to this point.</summary>
    public static void Mark(string stage)
    {
        if (!Enabled || _processStart == default) return;

        var elapsed = (DateTime.Now - _processStart).TotalMilliseconds;
        lock (Stages) Stages.Append($"{stage}={elapsed:F1} ");
    }

    /// <summary>Writes one line of stage timings and stops recording.</summary>
    public static void Flush()
    {
        if (!Enabled) return;

        try
        {
            string line;
            lock (Stages)
            {
                if (Stages.Length == 0) return;
                line = Stages.ToString().TrimEnd();
                Stages.Clear();
            }

            File.AppendAllText(LogPath!, line + Environment.NewLine);
        }
        catch
        {
            // Instrumentation must never affect a real launch.
        }
    }
}
