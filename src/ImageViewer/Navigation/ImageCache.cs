using ImageViewer.Imaging;

namespace ImageViewer.Navigation;

/// <summary>
/// Least-recently-used cache of decoded images, bounded by total pixel memory.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes Space and the mouse wheel feel instant: combined with the prefetcher, the
/// image the user is about to ask for has usually already been decoded and is a dictionary lookup
/// away rather than a disk read plus a decode.
/// </para>
/// <para>
/// The bound is on bytes rather than item count, because a folder can hold anything from 50 KB
/// thumbnails to 100 MP panoramas and a fixed item count would either waste memory or thrash.
/// All members are thread-safe: prefetch threads write while the UI thread reads.
/// </para>
/// </remarks>
public sealed class ImageCache
{
    private sealed record Entry(string Key, DecodedImage Image, long Bytes);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Most-recently-used at the head, eviction candidates at the tail.</summary>
    private readonly LinkedList<Entry> _lru = new();

    private long _bytes;

    public long BudgetBytes { get; }

    public ImageCache(long? budgetBytes = null)
    {
        BudgetBytes = budgetBytes ?? DefaultBudget();
    }

    /// <summary>
    /// A quarter of physical memory, clamped to a sane range.
    /// </summary>
    /// <remarks>
    /// Generous enough to hold a good run of prefetched images, but capped so a viewer left open on
    /// a folder of RAW files does not quietly consume gigabytes on a workstation that is also
    /// running a test bench.
    /// </remarks>
    private static long DefaultBudget()
    {
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0) total = 8L << 30;   // assume 8 GB when the runtime will not say

        var quarter = total / 4;
        return Math.Clamp(quarter, 256L << 20, 1536L << 20);
    }

    public long CurrentBytes { get { lock (_gate) return _bytes; } }

    public int Count { get { lock (_gate) return _index.Count; } }

    /// <summary>
    /// Looks up a cached decode, promoting it to most-recently-used on a hit.
    /// </summary>
    /// <param name="minimumDecodeScale">
    /// Rejects an entry decoded too coarsely for the caller's needs - a hit downscaled for a small
    /// window is not good enough once the user has zoomed in.
    /// </param>
    public bool TryGet(string path, out DecodedImage image, double minimumDecodeScale = 0)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(path, out var node))
            {
                if (node.Value.Image.DecodeScale + 1e-9 >= minimumDecodeScale)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    image = node.Value.Image;
                    return true;
                }

                // Too coarse to be useful; drop it so the finer decode can take its place.
                Remove(node);
            }
        }

        image = null!;
        return false;
    }

    /// <summary>
    /// Stores a decode, evicting least-recently-used entries until the budget is respected.
    /// </summary>
    public void Put(DecodedImage image)
    {
        // Previews are deliberately not cached: they are placeholders that a full decode is about
        // to replace, and caching one would let a blurry thumbnail masquerade as the real image.
        if (image.IsPreview) return;

        var bytes = image.ApproximateBytes;

        lock (_gate)
        {
            if (_index.TryGetValue(image.Path, out var existing))
                Remove(existing);

            // A single image larger than the whole budget is not worth evicting everything for.
            if (bytes > BudgetBytes) return;

            var node = _lru.AddFirst(new Entry(image.Path, image, bytes));
            _index[image.Path] = node;
            _bytes += bytes;

            while (_bytes > BudgetBytes && _lru.Last is { } tail)
                Remove(tail);
        }
    }

    /// <summary>Drops a specific path, for when a file is deleted, renamed or overwritten.</summary>
    public void Invalidate(string path)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(path, out var node)) Remove(node);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _index.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }

    /// <summary>Removes a node. The caller must hold <see cref="_gate"/>.</summary>
    private void Remove(LinkedListNode<Entry> node)
    {
        _lru.Remove(node);
        _index.Remove(node.Value.Key);
        _bytes -= node.Value.Bytes;
    }
}
