namespace ImageViewer;

/// <summary>
/// Process entry point.
/// </summary>
/// <remarks>
/// Deliberately touches no WPF type before <see cref="SingleInstance.TryHandOff"/> has decided
/// whether this process is going to show a window at all. A second launch therefore costs a process
/// start and a pipe write, and never pays to spin up a UI framework it is about to discard.
/// </remarks>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupTrace.Begin();
        StartupTrace.Mark("main");

        var paths = ParsePaths(args);

        if (SingleInstance.TryHandOff(paths))
            return 0;

        StartupTrace.Mark("solo");

        var app = new App(paths);
        return app.Run();
    }

    /// <summary>
    /// Pulls file and folder arguments out of the command line, ignoring switches.
    /// </summary>
    private static string[] ParsePaths(string[] args)
    {
        if (args.Length == 0) return [];

        List<string> paths = new(args.Length);

        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (arg.StartsWith('-') || arg.StartsWith('/') && arg.Length <= 3) continue;

            // Explorer passes paths already unquoted, but a hand-typed command line may not.
            paths.Add(arg.Trim('"'));
        }

        return [.. paths];
    }
}
