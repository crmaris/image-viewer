using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageViewer.Imaging;

/// <summary>
/// Plays animated GIFs, which WPF does not do on its own.
/// </summary>
/// <remarks>
/// <para>
/// A GIF is not simply a list of full images. Frames may cover only part of the canvas, and each
/// carries a disposal method saying what to do with the previous frame's pixels afterwards.
/// Rendering each frame in isolation - the naive approach - produces flickering fragments on any
/// GIF that uses partial frames, which is most of them.
/// </para>
/// <para>
/// Frames are composited once, up front, into complete images. GIFs are small enough that this is
/// cheaper than compositing on every tick, and it makes playback a simple index change.
/// </para>
/// </remarks>
public sealed class GifAnimator : IDisposable
{
    /// <summary>Refuse to pre-composite beyond this, to avoid a huge GIF exhausting memory.</summary>
    private const long MaxCompositeBytes = 256L << 20;

    /// <summary>
    /// Browsers clamp very short delays, and GIFs in the wild rely on that behaviour: a declared
    /// 0 means "as fast as possible", which in practice everyone renders at about 10 fps.
    /// </summary>
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(100);

    private readonly List<BitmapSource> _frames;
    private readonly List<TimeSpan> _delays;
    private readonly DispatcherTimer _timer;
    private int _index;

    /// <summary>Raised on the UI thread when the displayed frame should change.</summary>
    public event Action<BitmapSource>? FrameChanged;

    public int FrameCount => _frames.Count;

    private GifAnimator(List<BitmapSource> frames, List<TimeSpan> delays)
    {
        _frames = frames;
        _delays = delays;

        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// Builds an animator, or returns null when the file is not an animation worth playing.
    /// </summary>
    public static GifAnimator? TryCreate(byte[] bytes, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count < 2) return null;   // a single frame is a still image

            var (canvasWidth, canvasHeight) = ReadLogicalScreen(decoder);
            if (canvasWidth <= 0 || canvasHeight <= 0) return null;

            var estimated = (long)canvasWidth * canvasHeight * 4 * decoder.Frames.Count;
            if (estimated > MaxCompositeBytes) return null;

            var frames = Composite(decoder, canvasWidth, canvasHeight, out var delays, ct);
            return frames.Count < 2 ? null : new GifAnimator(frames, delays);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // A GIF we cannot animate still displays as a still image, which is an acceptable
            // outcome; never fail the open over it.
            return null;
        }
    }

    /// <summary>Composites every frame onto a running canvas, honouring disposal methods.</summary>
    private static List<BitmapSource> Composite(
        BitmapDecoder decoder, int width, int height, out List<TimeSpan> delays, CancellationToken ct)
    {
        var results = new List<BitmapSource>(decoder.Frames.Count);
        delays = new List<TimeSpan>(decoder.Frames.Count);

        var stride = width * 4;
        var canvas = new byte[(long)stride * height];
        byte[]? saved = null;

        foreach (var frame in decoder.Frames)
        {
            ct.ThrowIfCancellationRequested();

            var meta = frame.Metadata as BitmapMetadata;
            var left = GetInt(meta, "/imgdesc/Left");
            var top = GetInt(meta, "/imgdesc/Top");
            var disposal = GetInt(meta, "/grctlext/Disposal");
            var delayHundredths = GetInt(meta, "/grctlext/Delay");

            delays.Add(NormaliseDelay(delayHundredths));

            // Disposal 3 means "restore what was here before this frame", so snapshot first.
            if (disposal == 3) saved = (byte[])canvas.Clone();

            DrawFrame(frame, canvas, width, height, stride, left, top);

            var snapshot = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, (byte[])canvas.Clone(), stride);
            snapshot.Freeze();
            results.Add(snapshot);

            switch (disposal)
            {
                case 2:
                    // Restore to background: clear just this frame's rectangle.
                    ClearRect(canvas, width, height, stride, left, top, frame.PixelWidth, frame.PixelHeight);
                    break;
                case 3 when saved is not null:
                    Array.Copy(saved, canvas, canvas.Length);
                    break;
            }
        }

        return results;
    }

    /// <summary>Alpha-blends one frame onto the canvas at its declared offset.</summary>
    private static void DrawFrame(
        BitmapFrame frame, byte[] canvas, int canvasWidth, int canvasHeight, int stride, int left, int top)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var fw = converted.PixelWidth;
        var fh = converted.PixelHeight;
        var frameStride = fw * 4;
        var pixels = new byte[(long)frameStride * fh];
        converted.CopyPixels(pixels, frameStride, 0);

        for (var y = 0; y < fh; y++)
        {
            var canvasY = top + y;
            if (canvasY < 0 || canvasY >= canvasHeight) continue;

            for (var x = 0; x < fw; x++)
            {
                var canvasX = left + x;
                if (canvasX < 0 || canvasX >= canvasWidth) continue;

                var src = y * frameStride + x * 4;
                var alpha = pixels[src + 3];

                // GIF transparency is binary, so anything not fully opaque leaves the canvas
                // showing through rather than being blended.
                if (alpha == 0) continue;

                var dst = canvasY * stride + canvasX * 4;
                canvas[dst] = pixels[src];
                canvas[dst + 1] = pixels[src + 1];
                canvas[dst + 2] = pixels[src + 2];
                canvas[dst + 3] = alpha;
            }
        }
    }

    private static void ClearRect(
        byte[] canvas, int canvasWidth, int canvasHeight, int stride, int left, int top, int w, int h)
    {
        for (var y = top; y < Math.Min(top + h, canvasHeight); y++)
        {
            if (y < 0) continue;
            var x0 = Math.Max(0, left);
            var x1 = Math.Min(left + w, canvasWidth);
            if (x1 <= x0) continue;
            Array.Clear(canvas, y * stride + x0 * 4, (x1 - x0) * 4);
        }
    }

    private static (int Width, int Height) ReadLogicalScreen(BitmapDecoder decoder)
    {
        var meta = decoder.Metadata as BitmapMetadata;
        var width = GetInt(meta, "/logscrdesc/Width");
        var height = GetInt(meta, "/logscrdesc/Height");

        // Some encoders omit the logical screen descriptor; the first frame is then authoritative.
        if (width <= 0 || height <= 0)
        {
            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
        }

        return (width, height);
    }

    private static TimeSpan NormaliseDelay(int hundredths)
    {
        if (hundredths <= 0) return DefaultDelay;
        var delay = TimeSpan.FromMilliseconds(hundredths * 10);
        return delay < MinimumDelay ? DefaultDelay : delay;
    }

    private static int GetInt(BitmapMetadata? meta, string query)
    {
        if (meta is null) return 0;
        try
        {
            return meta.GetQuery(query) switch
            {
                ushort u => u,
                short s => s,
                byte b => b,
                int i => i,
                _ => 0,
            };
        }
        catch
        {
            return 0;   // absent metadata is normal
        }
    }

    public void Start()
    {
        if (_frames.Count < 2) return;

        _index = 0;
        FrameChanged?.Invoke(_frames[0]);
        _timer.Interval = _delays[0];
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        _index = (_index + 1) % _frames.Count;
        FrameChanged?.Invoke(_frames[_index]);

        // Per-frame delays: a GIF may hold one frame far longer than the rest.
        _timer.Interval = _delays[_index];
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        FrameChanged = null;
        _frames.Clear();
    }
}
