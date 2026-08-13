using System.Windows;
using System.Windows.Threading;

namespace ImageViewer;

/// <summary>
/// The WPF application object, built without an App.xaml.
/// </summary>
/// <remarks>
/// There is no application resource dictionary and no implicit styling, so startup does not parse
/// any BAML before the window appears.
/// </remarks>
public sealed class App : Application
{
    private readonly string[] _initialPaths;
    private readonly CancellationTokenSource _lifetime = new();

    public App(string[] initialPaths)
    {
        _initialPaths = initialPaths;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        StartupTrace.Mark("startup");

        var window = new MainWindow();
        MainWindow = window;
        StartupTrace.Mark("ctor");

        // Show first, decode second: the window is on screen while the image is still being read,
        // rather than the user staring at nothing until the decode finishes.
        window.Show();
        StartupTrace.Mark("shown");

        if (_initialPaths.Length > 0)
            window.Open(_initialPaths[0]);
        else
            window.ShowWelcome();   // only here, so the common path never builds the text stack

        SingleInstance.PathsReceived += OnPathsReceived;
        SingleInstance.StartServer(_lifetime.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstance.PathsReceived -= OnPathsReceived;
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnExit(e);
    }

    /// <summary>Handles a path handed over by a later launch. Arrives on the pipe thread.</summary>
    private void OnPathsReceived(string[] paths)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is not MainWindow window) return;

            window.ActivateFromHandoff();
            if (paths.Length > 0) window.Open(paths[0]);
        });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A viewer that dies on one bad file is worse than one that reports it and carries on, so
        // UI-thread faults are surfaced and swallowed rather than taking the process down.
        MessageBox.Show(
            $"{e.Exception.Message}\n\n{e.Exception.GetType().Name}",
            "Image Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }
}
