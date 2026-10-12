using ImageViewer.Imaging;

namespace ImageViewer.Navigation;

/// <summary>
/// Front door for getting an image on screen: decoding, caching and prefetching behind one facade.
/// </summary>
/// <remarks>
/// The window asks for a path and gets pixels; whether that meant a dictionary lookup, a background
/// decode already in flight, or a cold read from disk is not its concern.
/// </remarks>
public sealed class ImagePipeline : IDisposable
{
    private readonly ImageLoader _loader;
    private readonly ImageCache _cache;
    private readonly Prefetcher _prefetcher;
    private readonly CancellationTokenSource _initialLifetime = new();
    private InitialLoad? _initialLoad;
    private bool _disposed;

    public ImagePipeline(ImageLoader? loader = null, long? cacheBudgetBytes = null)
    {
        _loader = loader ?? new ImageLoader();
        _cache = new ImageCache(cacheBudgetBytes);
        _prefetcher = new Prefetcher(_loader, _cache);
    }

    public long CacheBudgetBytes => _cache.BudgetBytes;
    public long CacheBytesUsed => _cache.CurrentBytes;
    public int CacheCount => _cache.Count;

    /// <summary>Starts the first file on a worker before Window.Show initializes the renderer.</summary>
    internal void PrepareInitialImage(string path, int maxWidth, int maxHeight)
    {
        if (_initialLoad is not null || _disposed) return;
        _initialLoad = new InitialLoad(_loader, path, maxWidth, maxHeight, _initialLifetime.Token);
    }

    /// <summary>
    /// Returns an already-decoded image, or false. Never touches the disk.
    /// </summary>
    /// <remarks>
    /// The window calls this first on every navigation so a prefetched neighbour can be painted
    /// synchronously, inside the same input event, with no await and therefore no dropped frame.
    /// </remarks>
    public bool TryGetCached(string path, out DecodedImage image, double minimumDecodeScale = 0) =>
        _cache.TryGet(path, out image, minimumDecodeScale);

    /// <summary>
    /// Gets an image, decoding it if it is not already cached.
    /// </summary>
    /// <param name="preview">
    /// Optional sink for a fast embedded-thumbnail placeholder, reported before the full decode
    /// completes. Ignored on a cache hit, where there is nothing to wait for.
    /// </param>
    public async Task<DecodedImage> GetAsync(
        string path, int maxWidth, int maxHeight, CancellationToken ct,
        IProgress<DecodedImage>? preview = null)
    {
        // A full-resolution request must not be satisfied by a downscaled cache entry, so it
        // demands a DecodeScale of 1.0; a fit-sized request accepts whatever is already there.
        var minimumScale = maxWidth == 0 && maxHeight == 0 ? 1.0 : 0.0;

        if (_cache.TryGet(path, out var cached, minimumScale))
            return cached;

        DecodedImage image;
        var initial = _initialLoad is { } pending &&
            string.Equals(path, pending.Path, StringComparison.OrdinalIgnoreCase)
            ? Interlocked.Exchange(ref _initialLoad, null) : null;
        if (initial is not null)
        {
            initial.ForwardPreview(preview, ct);
            try { image = await initial.Result.WaitAsync(ct).ConfigureAwait(false); }
            finally { initial.ForwardPreview(null, CancellationToken.None); }
            var requiredScale = maxWidth > 0 && maxHeight > 0
                ? Math.Min(1.0, Math.Min(maxWidth / (double)image.PixelWidth,
                    maxHeight / (double)image.PixelHeight)) : 1.0;
            // The real viewport is known only after Show (maximization and per-monitor DPI).
            // Re-decode if the provisional window bounds were too small for that viewport.
            if (image.DecodeScale + 1e-3 < requiredScale)
                image = await _loader.LoadWithPreviewAsync(path, maxWidth, maxHeight, preview, ct)
                    .ConfigureAwait(false);
        }
        else
        {
            image = await _loader.LoadWithPreviewAsync(path, maxWidth, maxHeight, preview, ct)
                .ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        _cache.Put(image);
        return image;
    }

    /// <summary>Gets the embedded thumbnail, for something to show while the real decode runs.</summary>
    public Task<DecodedImage?> GetPreviewAsync(string path, CancellationToken ct) =>
        _loader.LoadPreviewAsync(path, ct);

    /// <summary>Repoints the prefetch ring after the user navigates.</summary>
    public void UpdatePrefetchWindow(string[] files, int currentIndex, int maxWidth, int maxHeight) =>
        _prefetcher.Update(files, currentIndex, maxWidth, maxHeight);

    /// <summary>Forgets a path, after the file changed on disk or was deleted.</summary>
    public void Invalidate(string path) => _cache.Invalidate(path);

    /// <summary>
    /// Drops every cached decode.
    /// </summary>
    /// <remarks>
    /// Needed when the window is resized substantially: entries decoded for the old viewport are
    /// the wrong resolution for the new one, and keeping them would show a soft image until the
    /// user navigated away and back.
    /// </remarks>
    public void Clear() => _cache.Clear();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initialLifetime.Cancel();
        _initialLifetime.Dispose();
        _initialLoad = null;
        _prefetcher.Dispose();
    }

    private sealed class InitialLoad : IProgress<DecodedImage>
    {
        private readonly Lock _gate = new();
        private DecodedImage? _preview;
        private IProgress<DecodedImage>? _sink;
        private CancellationToken _consumerToken;
        public string Path { get; }
        public Task<DecodedImage> Result { get; }

        public InitialLoad(ImageLoader loader, string path, int width, int height, CancellationToken ct)
        {
            Path = path;
            Result = Task.Run(() => loader.LoadWithPreviewAsync(path, width, height, this, ct), ct);
            // Folder launches and superseding handoffs may never consume the initial file.
            _ = Result.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void Report(DecodedImage value)
        {
            lock (_gate)
            {
                _preview = value;
                if (!_consumerToken.IsCancellationRequested) _sink?.Report(value);
            }
        }

        public void ForwardPreview(IProgress<DecodedImage>? sink, CancellationToken ct)
        {
            lock (_gate)
            {
                _sink = sink;
                _consumerToken = ct;
                if (_preview is not null && !ct.IsCancellationRequested && !Result.IsCompleted)
                    sink?.Report(_preview);
            }
        }
    }
}
