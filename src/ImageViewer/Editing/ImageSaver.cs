using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Imaging;

namespace ImageViewer.Editing;

public enum SaveMethod
{
    /// <summary>JPEG EXIF orientation rewritten; compressed image data untouched, so zero loss.</summary>
    LosslessExif,
    /// <summary>Pixels decoded and re-encoded. Lossless for PNG/BMP/TIFF, lossy for JPEG.</summary>
    ReEncoded,
    /// <summary>Nothing to write.</summary>
    NoChange,
}

public sealed record SaveResult(SaveMethod Method, string Path, string Description);

/// <summary>
/// Writes the user's rotation and flips back to the file.
/// </summary>
/// <remarks>
/// <para>
/// Two things make this less trivial than it looks. First, the pixels on screen already have the
/// file's EXIF orientation baked in, so saving naively would apply that rotation a second time on
/// the next open; the fix is to compose the EXIF transform with the user's edits and write the
/// result with the orientation tag reset to 1.
/// </para>
/// <para>
/// Second, JPEG is lossy, so decoding and re-encoding would degrade the image every single time it
/// was rotated. <see cref="JpegBitmapEncoder"/> can rearrange the encoded blocks instead, which
/// avoids that entirely.
/// </para>
/// </remarks>
public static class ImageSaver
{
    /// <summary>Formats WPF can write back in place.</summary>
    private static readonly HashSet<string> WritableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".tif", ".tiff" };

    public static bool CanSaveInPlace(string path) =>
        WritableExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Applies the user's edits to the file on disk.
    /// </summary>
    /// <param name="forceReEncode">
    /// Physically rotate a JPEG's pixels instead of updating its orientation tag. Costs a
    /// re-compression, so only worth it for software that ignores EXIF.
    /// </param>
    /// <exception cref="NotSupportedException">The format cannot be written back in place.</exception>
    public static SaveResult Save(
        string path, bool flipHorizontal, bool flipVertical, int rotation,
        CancellationToken ct, bool forceReEncode = false)
    {
        var userEdit = Orientation.FromUserEdits(flipHorizontal, flipVertical, rotation);
        if (userEdit.IsIdentity)
            return new SaveResult(SaveMethod.NoChange, path, "Nothing to save.");

        if (!CanSaveInPlace(path))
            throw new NotSupportedException(
                $"{Path.GetExtension(path)} files cannot be written back in place. " +
                "The rotation is still applied on screen.");

        var bytes = File.ReadAllBytes(path);
        var extension = Path.GetExtension(path);
        var isJpeg = extension is ".jpg" or ".jpeg" or ".jpe" or ".jfif" ||
                     FormatSniffer.Identify(bytes, path) == ImageFormatKind.Jpeg;

        // The displayed pixels are (file pixels + EXIF). The user then applied their edit on top,
        // so what must end up described in the file is EXIF followed by the edit, as one transform.
        var exif = ReadExifOrientation(bytes);
        var total = Orientation.FromExif(exif).Then(userEdit);

        return isJpeg && !forceReEncode
            ? SaveJpegLossless(bytes, path, total)
            : SaveReEncoded(bytes, path, total, extension, isJpeg, ct);
    }

    /// <summary>
    /// Rewrites a JPEG's orientation tag, leaving the compressed image data alone.
    /// </summary>
    /// <remarks>
    /// The obvious approach - <see cref="JpegBitmapEncoder.Rotation"/> - is widely believed to be a
    /// lossless block transform, but measurement says otherwise: rotating a test JPEG through a
    /// full turn altered about a tenth of its bytes and grew the file by 18%, because the encoder
    /// re-compresses. Editing the EXIF field instead is the only way to rotate a JPEG with genuinely
    /// zero loss without shipping a native jpegtran binary, and it is effectively instant since no
    /// pixels are decoded at all.
    /// </remarks>
    private static SaveResult SaveJpegLossless(byte[] bytes, string path, Orientation total)
    {
        var updated = JpegOrientationWriter.SetOrientation(bytes, total.ToExif())
            ?? throw new InvalidDataException("The file is not a readable JPEG.");

        WriteAtomically(path, updated);

        return new SaveResult(
            SaveMethod.LosslessExif, path,
            "Saved losslessly (orientation tag only - the image data was not re-compressed).");
    }

    /// <summary>Decodes, transforms and re-encodes, physically rewriting the pixels.</summary>
    private static SaveResult SaveReEncoded(
        byte[] bytes, string path, Orientation total, string extension, bool isJpeg, CancellationToken ct)
    {
        using var source = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        BitmapSource image = decoder.Frames[0];
        ct.ThrowIfCancellationRequested();

        // Chained singly: TransformedBitmap rejects a TransformGroup.
        if (total.Mirror)
        {
            var flip = new ScaleTransform(-1, 1);
            flip.Freeze();
            image = new TransformedBitmap(image, flip);
        }

        if (total.Rotation != 0)
        {
            var rotate = new RotateTransform(total.Rotation);
            rotate.Freeze();
            image = new TransformedBitmap(image, rotate);
        }

        image.Freeze();

        BitmapEncoder encoder = extension.ToLowerInvariant() switch
        {
            ".png" => new PngBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            // Quality 100 keeps the damage as small as re-compression allows; the lossless EXIF
            // path is still the default for JPEG precisely because this cannot be made free.
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => new JpegBitmapEncoder { QualityLevel = 100 },
            _ => new PngBitmapEncoder(),
        };

        encoder.Frames.Add(BitmapFrame.Create(image));

        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        WriteAtomically(path, buffer.ToArray());

        var note = isJpeg
            ? "Saved with pixels physically rotated (JPEG re-compressed, so slight quality loss)."
            : $"Saved (re-encoded {extension.TrimStart('.').ToUpperInvariant()}, no quality loss).";

        return new SaveResult(SaveMethod.ReEncoded, path, note);
    }

    /// <summary>
    /// Writes via a temporary file and then replaces the original.
    /// </summary>
    /// <remarks>
    /// Writing straight over the source would destroy the only copy if the process died midway.
    /// File.Replace is atomic and preserves the original's creation time and ACLs, unlike a
    /// delete-then-move.
    /// </remarks>
    private static void WriteAtomically(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(contents);
                output.Flush(flushToDisk: true);
            }

            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    private static int ReadExifOrientation(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

            return decoder.Frames.Count > 0 ? WicDecoder.ReadExifOrientation(decoder.Frames[0]) : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static void TrySetQuery(BitmapMetadata metadata, string query, object value)
    {
        try { metadata.SetQuery(query, value); }
        catch { /* codec may not support the query; the pixels are still correct */ }
    }
}
