using System.IO;

namespace ImageViewer.Imaging;

/// <summary>
/// Turns a path into a <see cref="DecodedImage"/>, off the UI thread.
/// </summary>
/// <remarks>
/// This is the seam the caching and prefetching layer plugs into. Everything above it asks for an
/// image by path and target size and gets a frozen bitmap back; whether that involved touching the
/// disk is not the caller's concern.
/// </remarks>
public class ImageLoader
{
    /// <summary>
    /// Reads a file into memory once, for reuse across the header probe and the full decode.
    /// </summary>
    /// <remarks>
    /// Buffering up front rather than letting the codec read the <see cref="FileStream"/> directly
    /// avoids a scatter of small reads, lets the header probe and the decode share one read, and -
    /// most importantly - leaves the file unlocked, so delete and rename work on the image that is
    /// currently on screen.
    /// </remarks>
    protected static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 16,
            options: FileOptions.SequentialScan | FileOptions.Asynchronous);

        var length = fs.Length;
        if (length > int.MaxValue)
            throw new IOException($"File is too large to open ({length:N0} bytes).");

        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        return buffer;
    }

    /// <summary>
    /// Decodes <paramref name="path"/>, downscaling to about <paramref name="maxWidth"/> x
    /// <paramref name="maxHeight"/> physical pixels. Pass 0 for both to decode at full resolution.
    /// </summary>
    public virtual async Task<DecodedImage> LoadAsync(
        string path, int maxWidth, int maxHeight, CancellationToken ct)
    {
        var bytes = await ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // Decoding is CPU-bound and can take hundreds of milliseconds, so it never runs inline.
        return await Task.Run(
            () => DecodeBytes(bytes, path, maxWidth, maxHeight, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Files below this size decode fast enough that the preview stage is not worth its overhead.
    /// </summary>
    private const long PreviewWorthwhileBytes = 2L << 20;

    /// <summary>
    /// Decodes an image, reporting the embedded thumbnail first if there is one worth showing.
    /// </summary>
    /// <param name="preview">
    /// Receives the fast placeholder. Construct it on the UI thread - <see cref="Progress{T}"/>
    /// captures the synchronisation context, so the callback marshals back automatically.
    /// </param>
    /// <remarks>
    /// The file is read once and the bytes are shared between the thumbnail and the full decode.
    /// Calling the preview and full paths separately would read a 50 MB RAW off disk twice, which
    /// would cost more than the preview saves.
    /// </remarks>
    public virtual async Task<DecodedImage> LoadWithPreviewAsync(
        string path, int maxWidth, int maxHeight,
        IProgress<DecodedImage>? preview, CancellationToken ct)
    {
        var bytes = await ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (preview is not null && bytes.LongLength >= PreviewWorthwhileBytes)
        {
            var thumb = await Task.Run(
                () => WicDecoder.TryDecodeEmbeddedThumbnail(bytes, path, ct), ct).ConfigureAwait(false);

            if (thumb is not null && !ct.IsCancellationRequested)
                preview.Report(thumb);
        }

        ct.ThrowIfCancellationRequested();
        return await Task.Run(
            () => DecodeBytes(bytes, path, maxWidth, maxHeight, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Grabs just the embedded EXIF thumbnail, to put something on screen within a few milliseconds.
    /// </summary>
    /// <returns>Null when the file has no usable embedded preview.</returns>
    public virtual async Task<DecodedImage?> LoadPreviewAsync(string path, CancellationToken ct)
    {
        try
        {
            var bytes = await ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return await Task.Run(
                () => WicDecoder.TryDecodeEmbeddedThumbnail(bytes, path, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The decode itself, delegated to the tiered chain.
    /// </summary>
    protected virtual DecodedImage DecodeBytes(
        byte[] bytes, string path, int maxWidth, int maxHeight, CancellationToken ct) =>
        DecoderChain.Decode(bytes, path, maxWidth, maxHeight, ct);
}
