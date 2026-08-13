using ImageViewer.Imaging;

namespace ImageViewer.Navigation;

/// <summary>
/// Decodes the images either side of the current one, in the background, before they are asked for.
/// </summary>
/// <remarks>
/// <para>
/// Navigation feels instant only if the decode has already happened. This keeps a small ring of
/// neighbours warm in the <see cref="ImageCache"/>, biased forwards because most browsing goes
/// that way, so pressing Space is usually a cache hit and paints within a single frame.
/// </para>
/// <para>
/// Work is deliberately throttled and always cancellable: prefetching must never compete with the
/// image the user is actually waiting for, and it must abandon its queue the moment the user
/// navigates somewhere else.
/// </para>
/// </remarks>
public sealed class Prefetcher : IDisposable
{
    private readonly ImageLoader _loader;
    private readonly ImageCache _cache;

    /// <summary>Caps concurrent background decodes so prefetch cannot saturate the CPU.</summary>
    private readonly SemaphoreSlim _throttle;

    private readonly Lock _gate = new();
    private CancellationTokenSource? _generation;
    private bool _disposed;

    /// <summary>How many images ahead of the current position to keep warm.</summary>
    public int Ahead { get; init; } = 3;

    /// <summary>How many behind - fewer, since backwards browsing is less common.</summary>
    public int Behind { get; init; } = 1;

    public Prefetcher(ImageLoader loader, ImageCache cache, int maxConcurrency = 2)
    {
        _loader = loader;
        _cache = cache;
        _throttle = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    /// <summary>
    /// Repoints the prefetch window at <paramref name="currentIndex"/>.
    /// </summary>
    /// <remarks>
    /// Cancels everything still queued from the previous position first. Without that, spinning the
    /// wheel through a folder would pile up decodes for images the user has already flown past.
    /// </remarks>
    public void Update(string[] files, int currentIndex, int maxWidth, int maxHeight)
    {
        if (_disposed || files.Length == 0 || currentIndex < 0) return;

        CancellationToken ct;

        lock (_gate)
        {
            _generation?.Cancel();
            _generation?.Dispose();
            _generation = new CancellationTokenSource();
            ct = _generation.Token;
        }

        // Nearest neighbours first, so the most likely next press is ready soonest.
        foreach (var offset in EnumerateOffsets())
        {
            var index = currentIndex + offset;
            if (index < 0 || index >= files.Length) continue;

            var path = files[index];
            if (_cache.TryGet(path, out _)) continue;

            _ = PrefetchAsync(path, maxWidth, maxHeight, ct);
        }
    }

    /// <summary>Offsets ordered by distance, forwards before backwards at equal distance.</summary>
    private IEnumerable<int> EnumerateOffsets()
    {
        var max = Math.Max(Ahead, Behind);
        for (var distance = 1; distance <= max; distance++)
        {
            if (distance <= Ahead) yield return distance;
            if (distance <= Behind) yield return -distance;
        }
    }

    private async Task PrefetchAsync(string path, int maxWidth, int maxHeight, CancellationToken ct)
    {
        try
        {
            await _throttle.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // The window may have moved on while this was queued behind the throttle.
            if (ct.IsCancellationRequested || _cache.TryGet(path, out _)) return;

            var image = await _loader.LoadAsync(path, maxWidth, maxHeight, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested) _cache.Put(image);
        }
        catch (OperationCanceledException)
        {
            // Expected whenever the user navigates away.
        }
        catch
        {
            // A file that will not decode is the foreground path's problem to report; failing
            // quietly here avoids surfacing an error for an image the user never asked to see.
        }
        finally
        {
            _throttle.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            _generation?.Cancel();
            _generation?.Dispose();
            _generation = null;
        }

        _throttle.Dispose();
    }
}
