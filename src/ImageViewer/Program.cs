using ImageViewer.Cli;

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

        // Ahead of everything, and ahead of the single-instance handoff in particular. A command
        // has to run in *this* process and print to the console that invoked it; handing it to a
        // window that happens to already be open would produce no output and no exit code worth
        // reading. The check is a few string comparisons, so a normal launch pays nothing for it.
        if (CommandLine.IsCommand(args)) return CommandLine.Run(args);

        var options = LaunchOptions.Parse(args);

        if (SingleInstance.TryHandOff(options.Paths))
            return 0;

        StartupTrace.Mark("solo");

        var app = new App(options);
        return app.Run();
    }
}
