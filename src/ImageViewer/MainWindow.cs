using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ImageViewer.Editing;
using ImageViewer.Files;
using ImageViewer.Imaging;
using ImageViewer.Navigation;
using ImageViewer.Settings;
using ImageViewer.Ui;
using ImageViewer.Update;

namespace ImageViewer;

/// <summary>
/// The viewer shell: render surface, input handling and folder navigation.
/// </summary>
/// <remarks>
/// Built in code rather than XAML on purpose. The window is a handful of elements, and skipping
/// BAML parsing and the XAML type loader keeps a measurable slice off cold start - which is the
/// headline requirement for this application.
/// </remarks>
public sealed class MainWindow : Window
{
    private readonly Grid _root;
    private readonly Image _imageHost;

    /// <summary>
    /// Status/error text, created on first use rather than in the constructor.
    /// </summary>
    /// <remarks>
    /// Constructing a <see cref="TextBlock"/> is the first thing that touches WPF's text and font
    /// stack, which measured around 150 ms of cold start. The overwhelming majority of launches
    /// open an image and never show a message, so that cost should not be on the startup path.
    /// </remarks>
    private TextBlock? _message;

    private readonly ViewTransform _view = new();
    private readonly ImagePipeline _pipeline = new();
    private readonly MatrixTransform _matrix = new();

    /// <summary>
    /// Preferences carried over from the previous session.
    /// </summary>
    /// <remarks>
    /// Read here, on the startup path, because the window's size and position have to be known
    /// before it is shown - restoring them afterwards would be a visible jump. It is one short text
    /// file parsed by hand precisely so that this can be afforded; see <see cref="AppSettings"/>.
    /// </remarks>
    private readonly AppSettings _settings = AppSettings.Load();

    /// <summary>Receives embedded thumbnails; marshals to the UI thread via the captured context.</summary>
    private readonly Progress<DecodedImage> _previewSink;

    private string[] _files = [];
    private int _index = -1;
    private DecodedImage? _current;

    /// <summary>
    /// Path currently being shown, tracked separately from <see cref="_current"/> so the title bar
    /// keeps its filename and folder position even when the file failed to decode.
    /// </summary>
    private string? _currentPath;

    /// <summary>True when <see cref="_current"/> is a downscaled decode that a zoom-in should refine.</summary>
    private bool _currentIsDownscaled;

    /// <summary>
    /// True between asking for an image and having it, so the title can distinguish "still working"
    /// from "this file is broken".
    /// </summary>
    /// <remarks>
    /// Without this the title bar reports whatever <see cref="_current"/> happens to hold - either
    /// the previous image's dimensions under the new file's name, or "unreadable" for a file that
    /// is merely still decoding. Both were observed before this flag existed.
    /// </remarks>
    private bool _isLoading;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _scanCts;

    /// <summary>Watches the current folder so external adds/deletes refresh the list.</summary>
    private FileSystemWatcher? _folderWatcher;
    private string? _watchedFolder;
    private readonly DispatcherTimer _rescanDebounce;

    /// <summary>When the app itself last mutated a file, so its own watcher echo can be skipped.</summary>
    private DateTime _lastOwnWriteUtc = DateTime.MinValue;

    // Pan state.
    private bool _isPanning;
    private Point _panOrigin;

    // Restored when leaving fullscreen.
    private WindowState _preFullscreenState = WindowState.Normal;
    private WindowStyle _preFullscreenStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _preFullscreenResize = ResizeMode.CanResize;
    private bool _isFullscreen;

    /// <summary>Drops render quality while the user is actively zooming or panning.</summary>
    private readonly DispatcherTimer _interactionSettle;

    /// <summary>Coalesces rapid navigation so a fast wheel spin does not decode every image it passes.</summary>
    private readonly DispatcherTimer _navigationSettle;

    /// <summary>Path the debounced navigation will load when it fires.</summary>
    private string? _pendingPath;

    /// <summary>Viewport the cache was populated for, so a resize can invalidate stale decodes.</summary>
    private Size _cachedForViewport;

    /// <summary>Drives multi-frame GIFs; null for still images.</summary>
    private GifAnimator? _animator;

    // Overlays, all built on first use so an unopened panel costs nothing at startup.
    private InfoOverlay? _info;
    private Toast? _toast;
    private Filmstrip? _filmstrip;
    private ContextMenu? _rotationMenu;
    private MenuItem? _saveRotationMenuItem;
    private Button? _saveRotationButton;
    private bool _isSaving;
    private bool _infoVisible;

    /// <summary>Advances the slideshow; null unless one is running.</summary>
    private DispatcherTimer? _slideshow;

    /// <summary>Seconds between slideshow advances.</summary>
    private double _slideshowSeconds = 4;

    /// <summary>
    /// True when the command line asked for a slideshow that cannot start yet.
    /// </summary>
    /// <remarks>
    /// A slideshow needs more than one file, and the folder is scanned after the first image is
    /// already on screen - that ordering is what stops opening one photo out of a huge directory
    /// waiting on the directory listing. So the request is remembered and honoured once the scan
    /// lands.
    /// </remarks>
    private bool _slideshowRequested;

    /// <summary>Set once a background check has found a newer release; null otherwise.</summary>
    private UpdateInfo? _availableUpdate;
    private bool _updateInProgress;

    public MainWindow()
    {
        Title = "Image Viewer";
        RestorePlacement();
        Background = Brushes.Black;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _imageHost = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            // The matrix does all the placement; layout must not also try to size or centre it.
            Stretch = Stretch.Fill,
            RenderTransform = _matrix,
            Visibility = Visibility.Collapsed,
        };
        RenderOptions.SetBitmapScalingMode(_imageHost, BitmapScalingMode.HighQuality);

        _root = new Grid { ClipToBounds = true };
        // A Grid gives its Image a viewport-sized layout slot. WPF then clips a larger
        // full-resolution bitmap BEFORE applying our RenderTransform, cutting away
        // pixels that should become visible when scaled or panned. Canvas arranges the
        // image at its requested size; only the outer viewport should clip the result.
        var imageSurface = new Canvas();
        imageSurface.Children.Add(_imageHost);
        _root.Children.Add(imageSurface);
        Content = _root;

        _interactionSettle = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _interactionSettle.Tick += OnInteractionSettled;

        // 70 ms is long enough to swallow a burst of wheel events (which arrive 15-50 ms apart
        // during a spin) and short enough to be imperceptible on a single key press. It only ever
        // costs anything on a cache miss - a prefetched neighbour paints with no delay at all.
        _navigationSettle = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(70),
        };
        _navigationSettle.Tick += OnNavigationSettled;

        // Constructed on the UI thread so Progress<T> captures this context and reports back on it.
        _previewSink = new Progress<DecodedImage>(OnPreviewReady);

        _rescanDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _rescanDebounce.Tick += OnRescanDebounced;

        AllowDrop = true;
        Drop += OnDrop;
        DragOver += OnDragOver;
        SizeChanged += OnSizeChanged;
        DpiChanged += (_, _) => UpdateLayoutMatrix(recomputeFit: true);
        // Closing, not Closed: RestoreBounds is only meaningful while the window still exists.
        Closing += (_, e) => { if (_isSaving) e.Cancel = true; else SaveSettings(); };
        Closed += (_, _) => { StopAnimation(); _pipeline.Dispose(); _folderWatcher?.Dispose(); };
        ContentRendered += OnContentRendered;

        KeyDown += OnKeyDown;
        MouseWheel += OnMouseWheel;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonUp += OnMouseRightButtonUp;
        MouseMove += OnMouseMove;
        MouseDoubleClick += (_, _) => ToggleFullscreen();

        // Pinch-zoom and two-finger pan on touchscreens and precision touchpads.
        IsManipulationEnabled = true;
        ManipulationDelta += OnManipulationDelta;

        // Pasted images accumulate in the temp folder; clear out last week's on startup,
        // off the UI thread, so a daily Ctrl+V habit does not fill the disk.
        _ = Task.Run(CleanupStalePastes);
    }

    /// <summary>First frame is on screen; this is the number the startup budget is measured against.</summary>
    private void OnContentRendered(object? sender, EventArgs e)
    {
        StartupTrace.Mark("rendered");
        StartupTrace.Flush();

        // Only now, with the window up and the first image already decoding, is it acceptable to
        // think about anything as optional as an update check.
        ScheduleUpdateCheck();

        // Queued behind the decode rather than run here: none of it is worth a dropped frame on
        // the one path the user actually notices.
        Dispatcher.InvokeAsync(RestoreDeferredState, DispatcherPriority.ApplicationIdle);
    }

    // ----------------------------------------------------------- launch options

    /// <summary>
    /// Applies what the command line asked for, before the window is shown.
    /// </summary>
    /// <remarks>
    /// Command-line options deliberately win over the remembered session: someone who types
    /// <c>--fullscreen</c> means this launch, whatever the last one happened to leave behind. The
    /// guard matters because the settings may already have put the window into full screen, and
    /// toggling twice would take it straight back out.
    /// </remarks>
    public void ApplyLaunchOptions(LaunchOptions options)
    {
        if (options.Fullscreen && !_isFullscreen) ToggleFullscreen();

        if (options.SlideshowSeconds > 0)
            _slideshowSeconds = Math.Clamp(options.SlideshowSeconds, 1, 30);

        _slideshowRequested = options.Slideshow;
    }

    /// <summary>Starts a slideshow the command line asked for, once there is something to show.</summary>
    private void StartRequestedSlideshow()
    {
        if (!_slideshowRequested || _files.Length < 2 || _slideshow is not null) return;

        _slideshowRequested = false;
        ToggleSlideshow();
    }

    // --------------------------------------------------------------- settings

    /// <summary>
    /// Applies the remembered size, position and window state before the window is shown.
    /// </summary>
    private void RestorePlacement()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        _slideshowSeconds = Math.Clamp(_settings.SlideshowSeconds, 1, 30);

        if (IsPlacementReachable(_settings.WindowLeft, _settings.WindowTop, Width, Height))
        {
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // Order matters: fullscreen records the state it has to return to, so the maximised flag
        // has to be applied first or leaving fullscreen would drop the window to a restored size
        // it never had.
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        if (_settings.Fullscreen) ToggleFullscreen();
    }

    /// <summary>
    /// True if a remembered position still lands somewhere the user can reach.
    /// </summary>
    /// <remarks>
    /// A window saved on a monitor that has since been unplugged - or on a laptop later used
    /// undocked - would otherwise be restored into empty space: running, focusable, and completely
    /// invisible, with no way to drag it back. Requiring a decent patch of it to overlap the
    /// virtual desktop, and its title bar not to sit above the top edge, keeps it grabbable however
    /// the display arrangement changed between sessions.
    /// </remarks>
    private static bool IsPlacementReachable(double left, double top, double width, double height)
    {
        if (double.IsNaN(left) || double.IsNaN(top)) return false;
        if (width < 320 || height < 240) return false;

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        // Roughly a window button's worth of title bar in each direction.
        const double Grabbable = 120;

        var overlapWidth = Math.Min(left + width, screenRight) - Math.Max(left, screenLeft);
        var overlapHeight = Math.Min(top + height, screenBottom) - Math.Max(top, screenTop);

        return overlapWidth >= Grabbable && overlapHeight >= Grabbable && top >= screenTop;
    }

    /// <summary>Records the current state so the next launch can pick it up.</summary>
    private void SaveSettings()
    {
        // While maximised or in fullscreen, Left/Top/Width/Height describe the screen-filling
        // rectangle rather than the size to come back to. RestoreBounds is the one that round-trips.
        var bounds = WindowState == WindowState.Normal && !_isFullscreen
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds is { Width: > 0, Height: > 0 } &&
            !double.IsNaN(bounds.Left) && !double.IsInfinity(bounds.Left))
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }

        _settings.WindowMaximized = _isFullscreen
            ? _preFullscreenState == WindowState.Maximized
            : WindowState == WindowState.Maximized;

        _settings.Fullscreen = _isFullscreen;
        _settings.SlideshowSeconds = _slideshowSeconds;
        _settings.InfoVisible = _infoVisible;
        _settings.FilmstripVisible = _filmstrip is { Visibility: Visibility.Visible };

        _settings.Save();
    }

    /// <summary>
    /// Reopens the panels the last session left open, and registers with the shell on first run.
    /// </summary>
    /// <remarks>
    /// Deliberately not in the constructor. Building the info overlay is what first touches WPF's
    /// text and font stack - worth about 150 ms - and the shell registration opens several dozen
    /// registry keys. Both belong after the first frame rather than in front of it.
    /// </remarks>
    private void RestoreDeferredState()
    {
        if (_settings.InfoVisible && !_infoVisible) ToggleInfo();
        if (_settings.FilmstripVisible) ToggleFilmstrip();

        RegisterWithShellOnce();
    }

    /// <summary>
    /// Adds the viewer to the per-user "Open with" list, once per location it is run from.
    /// </summary>
    /// <remarks>
    /// This lives in the application rather than the installer because the list is per-user and an
    /// all-users install runs elevated: an installer writing it would populate the administrator's
    /// hive rather than the hive of whoever actually uses the viewer. Doing it here also covers the
    /// portable build, which has no installer to do it at all.
    /// </remarks>
    private void RegisterWithShellOnce()
    {
        var executable = OpenWithRegistration.ExecutablePath();
        if (executable is null) return;

        if (string.Equals(_settings.OpenWithRegisteredFor, executable, StringComparison.OrdinalIgnoreCase))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                OpenWithRegistration.Register(executable);
            }
            catch
            {
                // Never worth surfacing - the viewer opens files perfectly well without it.
                return;
            }

            // Recorded back on the UI thread so the settings object keeps a single writer.
            Dispatcher.BeginInvoke(() =>
            {
                _settings.OpenWithRegisteredFor = executable;
                _settings.Save();
            });
        });
    }

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private Size ViewportDip => new(
        Math.Max(1, _root.ActualWidth),
        Math.Max(1, _root.ActualHeight));

    // ---------------------------------------------------------------- opening

    /// <summary>
    /// Opens a path, which may be an image file or a folder.
    /// </summary>
    public async void Open(string path)
    {
        if (_isSaving) return;
        try
        {
            if (Directory.Exists(path))
            {
                await OpenFolderAsync(path).ConfigureAwait(true);
                return;
            }

            if (!File.Exists(path))
            {
                ShowMessage($"Not found:\n{path}");
                return;
            }

            // The requested image is decoded first and the folder scanned afterwards, so opening
            // one file out of a huge directory never waits on the directory listing.
            RequestShow(path, immediate: true);
            await RescanFolderAsync(Path.GetDirectoryName(path), path).ConfigureAwait(true);
            SchedulePrefetch();
            StartRequestedSlideshow();
        }
        catch (Exception ex)
        {
            ShowMessage($"Could not open:\n{path}\n\n{ex.Message}");
        }
    }

    private async Task OpenFolderAsync(string folder)
    {
        await RescanFolderAsync(folder, selectPath: null).ConfigureAwait(true);

        if (_files.Length == 0)
        {
            ShowMessage($"No images found in:\n{folder}");
            return;
        }

        _index = 0;
        RequestShow(_files[0], immediate: true);
        SchedulePrefetch();
        StartRequestedSlideshow();
    }

    /// <summary>Rebuilds the file list and locates <paramref name="selectPath"/> within it.</summary>
    private async Task RescanFolderAsync(string? folder, string? selectPath)
    {
        if (string.IsNullOrEmpty(folder)) return;

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        try
        {
            var files = await FolderScanner.ScanAsync(folder, _scanCts.Token).ConfigureAwait(true);
            _files = files;

            if (selectPath is not null)
            {
                _index = Array.FindIndex(
                    files, f => string.Equals(f, selectPath, StringComparison.OrdinalIgnoreCase));

                // The opened file can legitimately be missing from the list - it may be hidden, or
                // have an extension we do not enumerate. Keep showing it as a standalone image.
                if (_index < 0) _files = [];
            }

            WatchFolder(folder);
            _filmstrip?.SetFiles(_files);
            _filmstrip?.SetCurrentIndex(_index);
            UpdateTitle();
            RefreshInfo();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scan.
        }
    }

    /// <summary>Starts watching <paramref name="folder"/> for external changes.</summary>
    private void WatchFolder(string folder)
    {
        if (string.Equals(_watchedFolder, folder, StringComparison.OrdinalIgnoreCase) && _folderWatcher is not null)
            return;

        _folderWatcher?.Dispose();
        _folderWatcher = null;
        _watchedFolder = folder;

        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            watcher.Created += (_, _) => ScheduleRescan();
            watcher.Deleted += (_, _) => ScheduleRescan();
            watcher.Renamed += (_, _) => ScheduleRescan();
            watcher.Changed += (_, _) => ScheduleRescan();
            _folderWatcher = watcher;
        }
        catch
        {
            // Watching is a convenience; an unwatched folder still browses fine.
            _folderWatcher = null;
            _watchedFolder = null;
        }
    }

    private void ScheduleRescan()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _rescanDebounce.Stop();
            _rescanDebounce.Start();
        });
    }

    private void OnRescanDebounced(object? sender, EventArgs e)
    {
        _rescanDebounce.Stop();
        if (_watchedFolder is null || _isSaving) return;
        // The app's own save/delete/rename already updated the list and cache explicitly;
        // a rescan here would be a no-op echo of our own write.
        if (DateTime.UtcNow - _lastOwnWriteUtc < TimeSpan.FromSeconds(1.5)) return;
        var current = _currentPath;
        var folder = _watchedFolder;
        _ = RescanAndRestoreAsync(folder, current);
    }

    /// <summary>Records an app-initiated file mutation so the folder watcher ignores its echo.</summary>
    private void NoteOwnWrite() => _lastOwnWriteUtc = DateTime.UtcNow;

    private async Task RescanAndRestoreAsync(string folder, string? current)
    {
        var before = _files;
        await RescanFolderAsync(folder, selectPath: null).ConfigureAwait(true);
        if (_files.Length == 0) return;

        // Drop cached decodes for files that no longer exist so a deleted image can never
        // reappear from the cache.
        if (before.Length > 0)
        {
            var stillThere = new HashSet<string>(_files, StringComparer.OrdinalIgnoreCase);
            foreach (var old in before)
                if (!stillThere.Contains(old)) _pipeline.Invalidate(old);
        }

        if (current is not null)
        {
            var at = Array.FindIndex(
                _files, f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                _index = at;
                _filmstrip?.SetCurrentIndex(_index);
                UpdateTitle();
                RefreshInfo();
                SchedulePrefetch();
                return;
            }

            // Current file vanished (deleted/moved elsewhere): show whatever now sits at the
            // same position rather than a stale "unreadable".
            if (!File.Exists(current))
            {
                _index = Math.Clamp(_index, 0, _files.Length - 1);
                RequestShow(_files[_index], immediate: true);
                SchedulePrefetch();
            }
        }
    }

    // ---------------------------------------------------------------- display

    /// <summary>
    /// Puts <paramref name="path"/> on screen, as fast as the situation allows.
    /// </summary>
    /// <remarks>
    /// Three tiers, fastest first: a prefetched image paints synchronously inside the current input
    /// event with no await at all; otherwise the decode is debounced so a fast wheel spin does not
    /// queue work for every image it flies past; and once running, an embedded thumbnail fills the
    /// gap until the full decode lands.
    /// </remarks>
    private void RequestShow(string path, bool immediate)
    {
        if (_isSaving) return;
        _currentPath = path;

        // Tier 1: already decoded. Painting synchronously here is what keeps navigation inside a
        // single frame - an await, even on a completed task, would push it to the next one.
        if (_pipeline.TryGetCached(path, out var cached))
        {
            CancelPendingLoad();
            _view.Reset();
            _isLoading = false;
            _current = cached;
            _currentIsDownscaled = cached.DecodeScale < 1.0;
            Present(cached);
            SchedulePrefetch();
            TryStartAnimation(path);
            return;
        }

        // Tier 2: not cached. Report where the user is straight away, but as "loading" rather than
        // reusing the previous image's details under the new file's name.
        _isLoading = true;
        _current = null;
        UpdateTitle();
        _pendingPath = path;
        _navigationSettle.Stop();

        if (immediate) OnNavigationSettled(this, EventArgs.Empty);
        else _navigationSettle.Start();
    }

    private void OnNavigationSettled(object? sender, EventArgs e)
    {
        _navigationSettle.Stop();

        var path = _pendingPath;
        _pendingPath = null;
        if (path is not null) _ = ShowImageAsync(path);
    }

    private async Task ShowImageAsync(string path)
    {
        CancelPendingLoad();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        var dpi = DpiScale;
        var viewport = ViewportDip;
        var maxW = (int)Math.Ceiling(viewport.Width * dpi);
        var maxH = (int)Math.Ceiling(viewport.Height * dpi);

        _currentPath = path;
        _view.Reset();

        try
        {
            var image = await _pipeline
                .GetAsync(path, maxW, maxH, ct, _previewSink)
                .ConfigureAwait(true);

            // Both the token and the path are checked. Cancellation alone is not enough: a decode
            // already inside a native library does not stop on request, so a superseded load can
            // still run to completion and must not be allowed to publish its result.
            if (ct.IsCancellationRequested || !IsStillCurrent(path)) return;

            _isLoading = false;
            _current = image;
            _currentIsDownscaled = image.DecodeScale < 1.0;
            _cachedForViewport = viewport;
            Present(image);
            SchedulePrefetch();
            TryStartAnimation(path);
        }
        catch (OperationCanceledException)
        {
            // The user moved on before this finished; the newer load owns the screen now.
        }
        catch (Exception ex)
        {
            // Same reasoning as the success path: a slow failure for an image the user has already
            // navigated away from must not overwrite whatever is on screen now. This was observed
            // reporting a healthy GIF as unreadable because a corrupt file's decode finished late.
            if (!IsStillCurrent(path)) return;

            _isLoading = false;
            _current = null;
            _imageHost.Visibility = Visibility.Collapsed;
            ShowMessage($"Could not display:\n{Path.GetFileName(path)}\n\n{ex.Message}");
            UpdateTitle();
            RefreshInfo();
        }
    }

    /// <summary>True if <paramref name="path"/> is still the image the user is looking at.</summary>
    private bool IsStillCurrent(string path) =>
        string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shows the embedded thumbnail while the full decode is still running.
    /// </summary>
    /// <remarks>
    /// Reports the full frame's dimensions, so the fit zoom it lands at is identical to the one the
    /// real decode will use and the swap is invisible rather than a jump.
    /// </remarks>
    private void OnPreviewReady(DecodedImage preview)
    {
        // A slow preview for an image the user has already navigated past must not overwrite the
        // one now on screen.
        if (!string.Equals(preview.Path, _currentPath, StringComparison.OrdinalIgnoreCase)) return;
        if (_current is { IsPreview: false }) return;

        _current = preview;
        _currentIsDownscaled = true;
        Present(preview);
    }

    private void CancelPendingLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    // -------------------------------------------------------------- animation

    /// <summary>
    /// Starts playback if the file turns out to be a multi-frame GIF.
    /// </summary>
    /// <remarks>
    /// Runs after the still image is already on screen, so a GIF appears instantly and begins
    /// moving a moment later rather than delaying the first paint while frames are composited.
    /// </remarks>
    private async void TryStartAnimation(string path)
    {
        if (!path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);

            // The user may have navigated on while this was reading.
            if (!string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase)) return;

            var animator = await Task.Run(
                () => GifAnimator.TryCreate(bytes, CancellationToken.None)).ConfigureAwait(true);

            if (animator is null) return;

            if (!string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                animator.Dispose();
                return;
            }

            StopAnimation();
            _animator = animator;
            animator.FrameChanged += OnAnimationFrame;
            animator.Start();
        }
        catch
        {
            // A GIF that will not animate still shows its first frame, which is good enough.
        }
    }

    private void OnAnimationFrame(System.Windows.Media.Imaging.BitmapSource frame)
    {
        // Frames are all the logical screen size, so only the source changes - the layout matrix
        // and zoom stay exactly as the user left them.
        _imageHost.Source = frame;
    }

    private void StopAnimation()
    {
        if (_animator is null) return;

        _animator.FrameChanged -= OnAnimationFrame;
        _animator.Dispose();
        _animator = null;
    }

    /// <summary>Points the prefetch ring at the current position.</summary>
    private void SchedulePrefetch()
    {
        if (_files.Length < 2 || _index < 0) return;

        var dpi = DpiScale;
        var viewport = ViewportDip;
        _pipeline.UpdatePrefetchWindow(
            _files, _index,
            (int)Math.Ceiling(viewport.Width * dpi),
            (int)Math.Ceiling(viewport.Height * dpi));
    }

    /// <summary>
    /// Handles a resize, discarding decodes made for a substantially different viewport.
    /// </summary>
    /// <remarks>
    /// Cached images are decoded to fit the window they were opened in. After a large resize those
    /// entries are the wrong resolution - too soft when the window grew - so they are dropped and
    /// re-decoded. Small changes are ignored, since clearing on every drag pixel would make
    /// resizing crawl.
    /// </remarks>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutMatrix(recomputeFit: true);

        if (_cachedForViewport.Width <= 0) return;

        var grewBy = Math.Max(
            e.NewSize.Width / Math.Max(1, _cachedForViewport.Width),
            e.NewSize.Height / Math.Max(1, _cachedForViewport.Height));

        if (grewBy < 1.25) return;

        _pipeline.Clear();
        _cachedForViewport = e.NewSize;
        if (_currentPath is not null) RequestShow(_currentPath, immediate: false);
    }

    private void Present(DecodedImage image)
    {
        // Any animation belongs to the outgoing image; stop it before the new one takes over.
        StopAnimation();

        if (_message is not null) _message.Visibility = Visibility.Collapsed;
        _imageHost.Visibility = Visibility.Visible;

        _imageHost.Source = image.Bitmap;
        // Explicit size in DIPs equal to the decoded pixel size; the matrix scales from there.
        _imageHost.Width = image.Bitmap.PixelWidth;
        _imageHost.Height = image.Bitmap.PixelHeight;

        UpdateLayoutMatrix(recomputeFit: true);
        UpdateTitle();
        RefreshInfo();
        _filmstrip?.SetCurrentIndex(_index);
    }

    /// <summary>Recomputes and applies the render matrix.</summary>
    private void UpdateLayoutMatrix(bool recomputeFit)
    {
        if (_current is null) return;

        if (recomputeFit) _view.ResolveZoom(_current, ViewportDip, DpiScale);
        _view.ClampPan(_current, ViewportDip, DpiScale);

        _matrix.Matrix = _view.BuildMatrix(_current, ViewportDip, DpiScale);
    }

    private void ShowMessage(string text)
    {
        EnsureMessageBlock().Text = text;
        _message!.Visibility = Visibility.Visible;
        _imageHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows the idle hint, for a launch with no file to open.</summary>
    public void ShowWelcome() =>
        ShowMessage("Open an image, or drop one here.\n\nSpace or mouse wheel to browse   -   Ctrl+wheel to zoom   -   F11 fullscreen   -   F1 shortcuts");

    private TextBlock EnsureMessageBlock()
    {
        if (_message is not null) return _message;

        _message = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)),
            FontSize = 15,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 700,
        };

        _root.Children.Add(_message);
        return _message;
    }

    private void UpdateTitle()
    {
        UpdateSaveButton();
        if (_currentPath is null)
        {
            Title = "Image Viewer";
            return;
        }

        var name = Path.GetFileName(_currentPath);
        var position = _files.Length > 1 ? $"  [{_index + 1}/{_files.Length}]" : string.Empty;

        // Keep the filename and folder position in all three states, so a broken or slow file in
        // the middle of a folder never leaves the user with no idea where they are.
        var detail = (_current, _isLoading) switch
        {
            (not null, _) => $"{_current.PixelWidth}x{_current.PixelHeight}  {_view.Zoom * 100:0}%",
            (null, true) => "loading...",
            _ => "unreadable",
        };

        Title = $"{name}{position}  -  {detail}  -  Image Viewer";
    }

    // ------------------------------------------------------------- navigation

    private void Navigate(int delta)
    {
        if (_isSaving) return;
        if (_files.Length == 0 || delta == 0) return;

        // Wrapping means a wheel spin never dead-ends at the folder boundary.
        var next = (_index + delta) % _files.Length;
        if (next < 0) next += _files.Length;
        if (next == _index) return;

        _index = next;
        RequestShow(_files[_index], immediate: false);
    }

    private void GoTo(int index)
    {
        if (_isSaving) return;
        if (_files.Length == 0) return;
        var clamped = Math.Clamp(index, 0, _files.Length - 1);
        if (clamped == _index) return;

        _index = clamped;
        RequestShow(_files[_index], immediate: false);
    }

    // ------------------------------------------------------------------ input

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_isSaving) { e.Handled = true; return; }
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Space or Key.Right or Key.PageDown when !ctrl:
                Navigate(+1);
                break;

            case Key.Back or Key.Left or Key.PageUp when !ctrl:
                Navigate(-1);
                break;

            case Key.Home:
                GoTo(0);
                break;

            case Key.End:
                GoTo(_files.Length - 1);
                break;

            case Key.Left when ctrl:
                RotateView(-90);
                break;

            case Key.Right when ctrl:
                RotateView(+90);
                break;

            case Key.H when !ctrl:
                _view.ToggleFlipHorizontal();
                UpdateLayoutMatrix(recomputeFit: true);
                UpdateTitle();
                break;

            case Key.V when !ctrl:
                _view.ToggleFlipVertical();
                UpdateLayoutMatrix(recomputeFit: true);
                UpdateTitle();
                break;

            case Key.V when ctrl:
                PasteFromClipboard();
                break;

            case Key.D0 or Key.NumPad0:
                _view.SetFit();
                UpdateLayoutMatrix(recomputeFit: true);
                UpdateTitle();
                break;

            case Key.D1 or Key.NumPad1:
                _view.SetActualSize();
                UpdateLayoutMatrix(recomputeFit: true);
                UpdateTitle();
                RefineResolutionIfNeeded();
                break;

            case Key.OemPlus or Key.Add:
                ZoomBy(1.25);
                break;

            case Key.OemMinus or Key.Subtract:
                ZoomBy(1 / 1.25);
                break;

            case Key.F11:
                ToggleFullscreen();
                break;

            case Key.S when ctrl:
                SaveEdits(forceReEncode: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                break;

            case Key.U when ctrl:
                InstallUpdate();
                break;

            case Key.Delete:
                DeleteCurrent(permanent: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                break;

            case Key.C when ctrl:
                CopyToClipboard(pathOnly: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                break;

            case Key.F2:
                RenameCurrent();
                break;

            case Key.E when !ctrl:
                if (_currentPath is not null) ShellOps.ShowInExplorer(_currentPath);
                break;

            case Key.I when !ctrl:
                ToggleInfo();
                break;

            case Key.T when !ctrl:
                ToggleFilmstrip();
                break;

            case Key.S when !ctrl:
                ToggleSlideshow();
                break;

            case Key.OemPeriod when _slideshow is not null:
                AdjustSlideshowInterval(+1);
                break;

            case Key.OemComma when _slideshow is not null:
                AdjustSlideshowInterval(-1);
                break;

            case Key.F1:
            case Key.OemQuestion:
                ShowHelp();
                break;

            case Key.Escape:
                if (_slideshow is not null) ToggleSlideshow();
                else if (_isFullscreen) ToggleFullscreen();
                else Close();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    // -------------------------------------------------------------- commands

    /// <summary>Writes the on-screen rotation and flips back to the file.</summary>
    private async void SaveEdits(bool forceReEncode)
    {
        if (_isSaving || _isLoading || _currentPath is null || _current is null) return;

        if (!_view.HasUnsavedEdit)
        {
            ShowToast("Nothing to save - the image has not been rotated or flipped.");
            return;
        }

        var path = _currentPath;
        var flipH = _view.FlipHorizontal;
        var flipV = _view.FlipVertical;
        var rotation = _view.RotationDegrees;

        // Hold this view steady until the write and reload finish. This also prevents a
        // second save composing the same rotation onto the already-updated file.
        _isSaving = true;
        _root.IsEnabled = false;
        UpdateSaveButton();
        try
        {
            var result = await Task.Run(() => ImageSaver.Save(
                path, flipH, flipV, rotation, CancellationToken.None, forceReEncode)).ConfigureAwait(true);

            if (result.Method == SaveMethod.NoChange)
            {
                ShowToast(result.Description);
                return;
            }

            // The file on disk changed, so any cached decode of it is now wrong.
            _pipeline.Invalidate(path);
            _view.ResetEdits();
            NoteOwnWrite();

            ShowToast(result.Description);

            // Reload so what is on screen is what was actually written, not our approximation of it.
            if (string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
                await ShowImageAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, isError: true);
        }
        finally
        {
            _isSaving = false;
            _root.IsEnabled = true;
            UpdateSaveButton();
        }
    }

    private void UpdateSaveButton()
    {
        var visible = _isSaving || (_current is not null && !_isLoading && _view.HasUnsavedEdit);
        if (_saveRotationButton is null && visible)
        {
            _saveRotationButton = new Button
            {
                Content = "Save rotation",
                ToolTip = "Save rotation and flips to this file (Ctrl+S)",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12),
                Padding = new Thickness(16, 9, 16, 9),
                FontSize = 14,
                Focusable = false,
            };
            _saveRotationButton.Click += (_, e) => { e.Handled = true; SaveEdits(false); };
            Panel.SetZIndex(_saveRotationButton, 100);
            _root.Children.Add(_saveRotationButton);
        }
        if (_saveRotationButton is not null)
        {
            _saveRotationButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            _saveRotationButton.Content = _isSaving ? "Saving…" : "Save rotation";
            _saveRotationButton.IsEnabled = !_isSaving;
        }
    }

    /// <summary>Sends the current file to the Recycle Bin and moves on.</summary>
    private void DeleteCurrent(bool permanent)
    {
        if (_currentPath is null) return;

        var path = _currentPath;
        var name = Path.GetFileName(path);

        // Permanent deletion is unrecoverable, so it is the one action that asks first.
        if (permanent)
        {
            var confirm = MessageBox.Show(
                $"Permanently delete '{name}'?\n\nThis cannot be undone.",
                "Image Viewer", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes) return;
        }

        var deleted = permanent
            ? ShellOps.DeletePermanently(path)
            : ShellOps.MoveToRecycleBin(path);

        if (!deleted)
        {
            ShowToast($"Could not delete '{name}'.", isError: true);
            return;
        }

        _pipeline.Invalidate(path);
        ShowToast(permanent ? $"Deleted '{name}' permanently." : $"'{name}' moved to the Recycle Bin.");
        NoteOwnWrite();

        RemoveCurrentFromList();
    }

    /// <summary>Drops the deleted entry and shows whatever now occupies its position.</summary>
    private void RemoveCurrentFromList()
    {
        if (_files.Length == 0)
        {
            _current = null;
            _currentPath = null;
            ShowMessage("No image.");
            return;
        }

        var remaining = _files.Where((_, i) => i != _index).ToArray();
        _files = remaining;
        _filmstrip?.SetFiles(remaining);

        if (remaining.Length == 0)
        {
            _current = null;
            _currentPath = null;
            _index = -1;
            ShowMessage("No more images in this folder.");
            UpdateTitle();
            return;
        }

        // Staying at the same index lands on the next image, which is what culling wants.
        _index = Math.Min(_index, remaining.Length - 1);
        RequestShow(remaining[_index], immediate: true);
    }

    private void CopyToClipboard(bool pathOnly)
    {
        if (_currentPath is null) return;

        try
        {
            if (pathOnly)
            {
                Clipboard.SetText(_currentPath);
                ShowToast("Path copied.");
                return;
            }

            if (_current is null) return;

            // Put both the bitmap and the file on the clipboard: bitmap for pasting into an editor,
            // file drop so Explorer and mail clients can paste it as a file.
            var data = new DataObject();
            data.SetImage(_current.Bitmap);
            data.SetFileDropList([_currentPath]);
            Clipboard.SetDataObject(data, copy: true);

            ShowToast("Image copied.");
        }
        catch (Exception ex)
        {
            // The clipboard is a shared resource and another process can be holding it open.
            ShowToast($"Copy failed: {ex.Message}", isError: true);
        }
    }

    private void RenameCurrent()
    {
        if (_currentPath is null) return;

        var current = Path.GetFileName(_currentPath);
        var proposed = RenameDialog.Ask(this, current);
        if (proposed is null || proposed == current) return;

        try
        {
            var oldPath = _currentPath;
            var newPath = ShellOps.Rename(oldPath, proposed);

            _pipeline.Invalidate(oldPath);
            _currentPath = newPath;
            if (_index >= 0 && _index < _files.Length) _files[_index] = newPath;
            NoteOwnWrite();

            // Renaming can change where the file sorts, so restore the folder's natural order.
            Array.Sort(_files, FolderScanner.CompareNatural);
            _index = Array.IndexOf(_files, newPath);
            _filmstrip?.SetFiles(_files);
            _filmstrip?.SetCurrentIndex(_index);

            UpdateTitle();
            ShowToast($"Renamed to '{proposed}'.");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, isError: true);
        }
    }

    /// <summary>Opens an image or file list from the clipboard (Ctrl+V).</summary>
    private void PasteFromClipboard()
    {
        if (_isSaving) return;

        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0 && !string.IsNullOrEmpty(files[0]))
                {
                    Open(files[0]!);
                    return;
                }
            }

            if (!Clipboard.ContainsImage())
            {
                ShowToast("Clipboard has no image to paste.");
                return;
            }

            var image = Clipboard.GetImage();
            if (image is null)
            {
                ShowToast("Clipboard has no image to paste.", isError: true);
                return;
            }

            var folder = Path.Combine(Path.GetTempPath(), "ImageViewerPaste");
            Directory.CreateDirectory(folder);
            var temp = Path.Combine(folder, $"pasted-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read))
                encoder.Save(stream);

            Open(temp);
        }
        catch (Exception ex)
        {
            ShowToast($"Paste failed: {ex.Message}", isError: true);
        }
    }

    /// <summary>Deletes pasted images older than a week from the temp folder.</summary>
    private static void CleanupStalePastes()
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "ImageViewerPaste");
            if (!Directory.Exists(folder)) return;

            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
            foreach (var file in Directory.EnumerateFiles(folder, "pasted-*.png"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch
                {
                    // A pasted image currently open elsewhere must be left alone.
                }
            }
        }
        catch
        {
            // Cleanup is best effort; it must never surface at startup.
        }
    }

    /// <summary>Shows every keyboard and mouse shortcut (F1 or ?).</summary>
    private void ShowHelp()
    {
        MessageBox.Show(
            "Space / Right / PageDown / wheel down — next image\n" +
            "Backspace / Left / PageUp / wheel up — previous image\n" +
            "Home / End — first / last image\n" +
            "Ctrl+wheel or pinch — zoom at cursor\n" +
            "+ / - or 0 / 1 — zoom in / out, fit, actual size\n" +
            "Left-drag or two-finger drag — pan\n" +
            "Ctrl+Left / Ctrl+Right — rotate 90°\n" +
            "H / V — flip horizontal / vertical\n" +
            "Ctrl+S / Ctrl+Shift+S — save rotation (lossless JPEG) / re-encoded\n" +
            "Right-click — rotate menu\n" +
            "Delete / Shift+Delete — Recycle Bin / delete permanently\n" +
            "Ctrl+C / Ctrl+Shift+C — copy image / copy path\n" +
            "Ctrl+V — paste image or file from clipboard\n" +
            "F2 — rename    E — show in Explorer\n" +
            "I / T / S — info / filmstrip / slideshow\n" +
            ", . during slideshow — slower / faster\n" +
            "F11 or double-click — fullscreen\n" +
            "Ctrl+U — install update\n" +
            "F1 or ? — this help    Esc — stop / leave / close",
            "Image Viewer shortcuts", MessageBoxButton.OK, MessageBoxImage.None);
    }

    /// <summary>Pinch-zoom and two-finger pan.</summary>
    private void OnManipulationDelta(object? sender, System.Windows.Input.ManipulationDeltaEventArgs e)
    {
        if (_current is null) return;

        BeginInteraction();
        var anchor = e.ManipulationOrigin;
        // ManipulationOrigin is relative to the event source; translate to viewport coordinates.
        var relative = _root.TranslatePoint(new Point(anchor.X, anchor.Y), _root);
        if (Math.Abs(e.DeltaManipulation.Scale.X - 1.0) > 0.001 ||
            Math.Abs(e.DeltaManipulation.Scale.Y - 1.0) > 0.001)
        {
            var factor = (e.DeltaManipulation.Scale.X + e.DeltaManipulation.Scale.Y) / 2.0;
            _view.ZoomAt(factor, new Point(relative.X, relative.Y), _current, ViewportDip, DpiScale);
        }
        if (e.DeltaManipulation.Translation.LengthSquared > 0)
            _view.Pan(e.DeltaManipulation.Translation.X, e.DeltaManipulation.Translation.Y, _current, ViewportDip, DpiScale);
        UpdateLayoutMatrix(recomputeFit: false);
        UpdateTitle();
        _interactionSettle.Stop();
        _interactionSettle.Start();
        e.Handled = true;
    }

    // ---------------------------------------------------------------- update

    /// <summary>
    /// Looks for a newer release in the background, long after the window is up.
    /// </summary>
    /// <remarks>
    /// Runs on a delay and off the startup path entirely: an update check must never be a reason
    /// the viewer is slow to open. Throttled to once a day, and completely silent when there is
    /// nothing to report or the network is unavailable.
    /// </remarks>
    private void ScheduleUpdateCheck()
    {
        if (!AppUpdateService.ShouldCheckNow()) return;

        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromSeconds(4),
        };

        timer.Tick += async (_, _) =>
        {
            timer.Stop();

            try
            {
                var update = await new AppUpdateService()
                    .CheckAsync(CancellationToken.None)
                    .ConfigureAwait(true);

                if (update is null) return;

                _availableUpdate = update;

                ShowToast(update.CanInstallAutomatically
                    ? $"Version {update.Version.ToString(3)} is available - press Ctrl+U to install."
                    : $"Version {update.Version.ToString(3)} is available - press Ctrl+U to open the release page.");
            }
            catch
            {
                // An update check is never worth interrupting the user over.
            }
        };

        timer.Start();
    }

    /// <summary>Downloads and runs the update, after confirming with the user.</summary>
    private async void InstallUpdate()
    {
        if (_updateInProgress) return;

        if (_availableUpdate is null)
        {
            ShowToast($"No update available. This is version {AppUpdateService.CurrentVersion.ToString(3)}.");
            return;
        }

        var update = _availableUpdate;

        // No installer asset, or a portable copy: send the user to the page rather than guessing.
        if (!update.CanInstallAutomatically)
        {
            AppUpdateService.OpenReleasePage(update.ReleasePageUrl);
            return;
        }

        var notes = string.IsNullOrWhiteSpace(update.Notes) ? "" : $"\n\n{update.Notes}";
        var confirm = MessageBox.Show(
            $"Version {update.Version.ToString(3)} is available " +
            $"(you have {AppUpdateService.CurrentVersion.ToString(3)}).\n\n" +
            $"Download {update.InstallerName} ({update.InstallerSizeBytes / (1024.0 * 1024):0.#} MB) " +
            $"and run the installer?\n\nImage Viewer will close so the update can be applied.{notes}",
            "Update Image Viewer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (confirm != MessageBoxResult.Yes) return;

        _updateInProgress = true;

        try
        {
            var progress = new Progress<double>(fraction =>
                ShowToast($"Downloading update... {fraction * 100:0}%"));

            var installer = await new AppUpdateService()
                .DownloadInstallerAsync(update, progress, CancellationToken.None)
                .ConfigureAwait(true);

            ShowToast("Starting the installer...");

            // Matched to how this copy was installed, so an all-users installation cannot be
            // "updated" into a second per-user copy sitting alongside the original.
            AppUpdateService.LaunchInstaller(installer, update.InstallerDigest!);

            // The installer cannot replace files this process holds open, so it has to close now.
            // Only after the launch has actually succeeded - closing first would leave the user
            // with no window and no installer if the elevation prompt were declined.
            Close();
        }
        catch (OperationCanceledException ex)
        {
            // Declining the elevation prompt is a decision, not a failure.
            _updateInProgress = false;
            ShowToast(ex.Message);
        }
        catch (Exception ex)
        {
            _updateInProgress = false;
            ShowToast($"Update failed: {ex.Message}", isError: true);
        }
    }

    // -------------------------------------------------------------- overlays

    private void ShowToast(string message, bool isError = false)
    {
        _toast ??= AddOverlay(new Toast());
        _toast.Show(message, isError);
    }

    private void ToggleInfo()
    {
        _info ??= AddOverlay(new InfoOverlay());
        _infoVisible = !_infoVisible;
        _info.Visibility = _infoVisible ? Visibility.Visible : Visibility.Collapsed;
        RefreshInfo();
    }

    private void RefreshInfo()
    {
        if (!_infoVisible || _info is null) return;

        // EXIF is read on demand rather than during decode: it is only ever needed when this panel
        // is open, which is rarely.
        var exif = _currentPath is not null ? ExifSummary.Read(_currentPath) : null;
        _info.Update(_current, _currentPath, _index, _files.Length, _view, exif);
    }

    private void ToggleFilmstrip()
    {
        if (_filmstrip is null)
        {
            _filmstrip = AddOverlay(new Filmstrip());
            _filmstrip.IndexSelected += OnFilmstripSelected;
            _filmstrip.SetFiles(_files);
        }

        _filmstrip.Toggle();
        _filmstrip.SetCurrentIndex(_index);
    }

    private void OnFilmstripSelected(int index) => GoTo(index);

    private T AddOverlay<T>(T element) where T : UIElement
    {
        _root.Children.Add(element);
        return element;
    }

    // ------------------------------------------------------------- slideshow

    private void ToggleSlideshow()
    {
        if (_slideshow is not null)
        {
            _slideshow.Stop();
            _slideshow = null;
            ShowToast("Slideshow stopped.");
            return;
        }

        if (_files.Length < 2)
        {
            ShowToast("Nothing to play - this folder has only one image.");
            return;
        }

        _slideshow = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(_slideshowSeconds),
        };
        _slideshow.Tick += (_, _) => Navigate(+1);
        _slideshow.Start();

        ShowToast($"Slideshow started - {_slideshowSeconds:0.#}s per image. " +
                  "Comma and period adjust, S or Esc stops.");
    }

    private void AdjustSlideshowInterval(double delta)
    {
        if (_slideshow is null) return;

        _slideshowSeconds = Math.Clamp(_slideshowSeconds + delta, 1, 30);
        _slideshow.Interval = TimeSpan.FromSeconds(_slideshowSeconds);
        ShowToast($"Slideshow: {_slideshowSeconds:0.#}s per image.");
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Ctrl+wheel zooms about the cursor; the plain wheel is reserved for navigation.
            ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15, e.GetPosition(_root));
        }
        else
        {
            Navigate(e.Delta > 0 ? -1 : +1);
        }

        e.Handled = true;
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_current is null) return;

        // Menus create text and font resources, so build this only after the user asks for it.
        // Keeping it out of the constructor preserves the measured cold-start path.
        if (_rotationMenu is null)
        {
            (_rotationMenu, _saveRotationMenuItem) = CreateRotationContextMenu(
                () => RotateView(-90),
                () => RotateView(+90),
                () => SaveEdits(forceReEncode: false));
        }

        _saveRotationMenuItem!.IsEnabled = !_isSaving && !_isLoading && _view.HasUnsavedEdit;
        _rotationMenu.PlacementTarget = _root;
        _rotationMenu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Builds the image menu on first right-click, never during startup.</summary>
    internal static (ContextMenu Menu, MenuItem SaveItem) CreateRotationContextMenu(
        Action rotateLeft, Action rotateRight, Action save)
    {
        ArgumentNullException.ThrowIfNull(rotateLeft);
        ArgumentNullException.ThrowIfNull(rotateRight);
        ArgumentNullException.ThrowIfNull(save);

        var left = new MenuItem
        {
            Header = "Rotate left 90°",
            InputGestureText = "Ctrl+←",
        };
        left.Click += (_, _) => rotateLeft();

        var right = new MenuItem
        {
            Header = "Rotate right 90°",
            InputGestureText = "Ctrl+→",
        };
        right.Click += (_, _) => rotateRight();

        var saveItem = new MenuItem
        {
            Header = "Save rotation",
            InputGestureText = "Ctrl+S",
        };
        saveItem.Click += (_, _) => save();

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(left);
        menu.Items.Add(right);
        menu.Items.Add(new Separator());
        menu.Items.Add(saveItem);

        return (menu, saveItem);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_current is null) return;

        _isPanning = true;
        _panOrigin = e.GetPosition(_root);
        _imageHost.CaptureMouse();
        Cursor = Cursors.SizeAll;
        BeginInteraction();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || _current is null) return;

        var p = e.GetPosition(_root);
        _view.Pan(p.X - _panOrigin.X, p.Y - _panOrigin.Y, _current, ViewportDip, DpiScale);
        _panOrigin = p;

        UpdateLayoutMatrix(recomputeFit: false);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;

        _isPanning = false;
        _imageHost.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        EndInteraction();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            Open(paths[0]);

        e.Handled = true;
    }

    // ------------------------------------------------------------------- view

    private void RotateView(int degrees)
    {
        if (_isSaving || _current is null) return;

        _view.Rotate(degrees);
        UpdateLayoutMatrix(recomputeFit: true);
        UpdateTitle();
        RefreshInfo();
    }

    private void ZoomBy(double factor) => ZoomBy(factor, new Point(ViewportDip.Width / 2, ViewportDip.Height / 2));

    private void ZoomBy(double factor, Point anchor)
    {
        if (_current is null) return;

        BeginInteraction();
        _view.ZoomAt(factor, anchor, _current, ViewportDip, DpiScale);
        UpdateLayoutMatrix(recomputeFit: false);
        UpdateTitle();
        _interactionSettle.Stop();
        _interactionSettle.Start();
    }

    /// <summary>
    /// Re-decodes at full resolution once the user zooms past what the downscaled decode can show.
    /// </summary>
    /// <remarks>
    /// Decoding to viewport size is what makes the first paint fast, but it means the pixels for a
    /// deep zoom simply are not there. Rather than decode everything at full size up front, the
    /// full decode is deferred until a zoom actually needs it - which for most images never happens.
    /// </remarks>
    private async void RefineResolutionIfNeeded()
    {
        if (_current is null || !_currentIsDownscaled) return;

        // Only worth it once the requested zoom exceeds what the decoded bitmap actually holds.
        if (_view.Zoom <= _current.DecodeScale * 1.05) return;

        var path = _current.Path;
        CancelPendingLoad();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        try
        {
            var full = await _pipeline.GetAsync(path, 0, 0, ct).ConfigureAwait(true);
            // The user may have navigated while the full decode was running. Checking the
            // live current path (not the stale _current object) is what prevents a late
            // full-res A overwriting the B now on screen.
            if (ct.IsCancellationRequested || !IsStillCurrent(path)) return;

            _current = full;
            _currentIsDownscaled = false;

            _imageHost.Source = full.Bitmap;
            _imageHost.Width = full.Bitmap.PixelWidth;
            _imageHost.Height = full.Bitmap.PixelHeight;
            UpdateLayoutMatrix(recomputeFit: false);
        }
        catch (OperationCanceledException)
        {
            // Superseded; the downscaled decode stays on screen, which is still correct.
        }
        catch
        {
            // A failed refinement is cosmetic only - keep showing what we already have.
        }
    }

    private void BeginInteraction() =>
        RenderOptions.SetBitmapScalingMode(_imageHost, BitmapScalingMode.LowQuality);

    private void EndInteraction()
    {
        _interactionSettle.Stop();
        _interactionSettle.Start();
    }

    private void OnInteractionSettled(object? sender, EventArgs e)
    {
        _interactionSettle.Stop();
        RenderOptions.SetBitmapScalingMode(_imageHost, BitmapScalingMode.HighQuality);
        RefineResolutionIfNeeded();
    }

    /// <summary>
    /// Brings the window to the front after another launch handed a path over.
    /// </summary>
    /// <remarks>
    /// Without this the second double-click would appear to do nothing: the running instance would
    /// load the new image behind whatever window currently has focus.
    /// </remarks>
    public void ActivateFromHandoff()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _isFullscreen ? WindowState.Maximized : _preFullscreenState;

        Activate();

        // Windows refuses foreground activation to a process that did not receive the last input.
        // A momentary Topmost flip is the standard way to get the window in front anyway.
        var wasTopmost = Topmost;
        Topmost = true;
        Topmost = wasTopmost;

        Focus();
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _preFullscreenState = WindowState;
            _preFullscreenStyle = WindowStyle;
            _preFullscreenResize = ResizeMode;

            // Must drop out of Maximized first, otherwise the restyled window keeps the old
            // work-area bounds and leaves the taskbar visible.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
        }
        else
        {
            WindowStyle = _preFullscreenStyle;
            ResizeMode = _preFullscreenResize;
            WindowState = _preFullscreenState;
            _isFullscreen = false;
        }
    }
}
