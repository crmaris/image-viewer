using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Imaging;
using ImageViewer.Navigation;

namespace ImageViewer.SelfTest;

internal static class InitialImageChecks
{
    public static void Run(Action<string, bool, string?> check)
    {
        var full = Image("first.png", 10, false);
        var small = Image("first.png", 5, false);
        var thumb = Image("first.png", 2, true);

        using (var loader = new ControlledLoader(full, thumb))
        using (var pipeline = new ImagePipeline(loader))
        {
            pipeline.PrepareInitialImage("first.png", 10, 10);
            check("first file starts before the window requests it", loader.Started.Task.Wait(5000), null);
            var previews = new PreviewSink();
            var request = pipeline.GetAsync("first.png", 8, 8, CancellationToken.None, previews);
            check("an early embedded thumbnail is replayed while the first decode is pending",
                ReferenceEquals(previews.Last, thumb) && !request.IsCompleted, null);
            loader.Complete();
            var shown = request.GetAwaiter().GetResult();
            check("first image reuses the early decode without reading twice",
                ReferenceEquals(shown, full) && loader.Calls == 1 && pipeline.CacheCount == 1, null);
        }

        using (var loader = new ControlledLoader(small, full: full))
        using (var pipeline = new ImagePipeline(loader))
        {
            pipeline.PrepareInitialImage("first.png", 5, 5);
            loader.Complete();
            var shown = pipeline.GetAsync("first.png", 10, 10, CancellationToken.None).GetAwaiter().GetResult();
            check("a larger real viewport refines the provisional startup decode",
                shown.DecodeScale == 1 && loader.Calls == 2, null);
        }

        using (var loader = new ControlledLoader(full, thumb))
        using (var pipeline = new ImagePipeline(loader))
        using (var cancel = new CancellationTokenSource())
        {
            pipeline.PrepareInitialImage("first.png", 10, 10);
            var request = pipeline.GetAsync("first.png", 10, 10, cancel.Token);
            cancel.Cancel();
            var canceled = false;
            try { request.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { canceled = true; }
            loader.Complete();
            check("superseding the first request does not publish or cache its result",
                canceled && pipeline.CacheCount == 0, null);
        }

        using (var loader = new ControlledLoader(full))
        using (var pipeline = new ImagePipeline(loader))
        {
            pipeline.PrepareInitialImage("first.png", 10, 10);
            loader.Fail();
            var failed = false;
            try { pipeline.GetAsync("first.png", 10, 10, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (IOException) { failed = true; }
            check("an early read failure reaches the normal image error path", failed, null);
        }

        using (var loader = new ControlledLoader(full))
        {
            var pipeline = new ImagePipeline(loader);
            pipeline.PrepareInitialImage("first.png", 10, 10);
            check("initial read is in flight for the close test", loader.Started.Task.Wait(5000), null);
            pipeline.Dispose();
            pipeline.Dispose();
            check("closing cancels the startup worker and pipeline disposal stays idempotent",
                loader.Canceled.Task.Wait(5000), null);
        }
    }

    private static DecodedImage Image(string path, int pixels, bool preview)
    {
        var bitmap = BitmapSource.Create(pixels, pixels, 96, 96, PixelFormats.Bgra32,
            null, new byte[pixels * pixels * 4], pixels * 4);
        bitmap.Freeze();
        return new DecodedImage { Path = path, Bitmap = bitmap, PixelWidth = 10, PixelHeight = 10,
            IsPreview = preview, DecoderName = "startup fixture" };
    }

    private sealed class PreviewSink : IProgress<DecodedImage>
    {
        public DecodedImage? Last { get; private set; }
        public void Report(DecodedImage value) => Last = value;
    }

    private sealed class ControlledLoader(DecodedImage result, DecodedImage? thumb = null,
        DecodedImage? full = null) : ImageLoader, IDisposable
    {
        private readonly TaskCompletionSource<DecodedImage> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public void Complete() => _completion.TrySetResult(result);
        public void Fail() => _completion.TrySetException(new IOException("startup fixture read failure"));
        public override async Task<DecodedImage> LoadWithPreviewAsync(string path, int maxWidth,
            int maxHeight, IProgress<DecodedImage>? preview, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) > 1) return full ?? result;
            if (thumb is not null) preview?.Report(thumb);
            Started.TrySetResult(true);
            try { return await _completion.Task.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { Canceled.TrySetResult(true); throw; }
        }
        public void Dispose() => _completion.TrySetCanceled();
    }
}
