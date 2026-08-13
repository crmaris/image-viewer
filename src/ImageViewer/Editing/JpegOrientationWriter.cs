using System.Buffers.Binary;
using System.IO;

namespace ImageViewer.Editing;

/// <summary>
/// Changes a JPEG's orientation by editing its EXIF bytes, leaving the compressed image untouched.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <see cref="System.Windows.Media.Imaging.JpegBitmapEncoder"/>'s Rotation
/// property, despite its reputation, silently re-encodes: rotating a test JPEG through a full turn
/// changed roughly a tenth of its bytes and grew the file by 18%. Rotating an image a few times
/// would visibly degrade it. ImageViewer.SelfTest verifies both facts - that the encoder loses data
/// and that this class does not.
/// </para>
/// <para>
/// A JPEG is a chain of marker segments followed by entropy-coded scan data. Orientation lives in a
/// single 2-byte field inside the APP1/Exif segment, so changing it means rewriting those two bytes
/// and copying everything else verbatim. The compressed image data is never decoded, so the result
/// is bit-for-bit identical apart from the tag - truly lossless, and near-instant regardless of
/// image size.
/// </para>
/// <para>
/// The trade-off is that the pixels stay as stored and the tag tells viewers how to present them.
/// Every current browser, Explorer, Windows Photos and the major editors honour it; software that
/// ignores EXIF will show the image unrotated.
/// </para>
/// </remarks>
public static class JpegOrientationWriter
{
    private const ushort OrientationTag = 0x0112;
    private const ushort TypeShort = 3;

    /// <summary>
    /// Returns a copy of <paramref name="jpeg"/> with its EXIF orientation set.
    /// </summary>
    /// <returns>The rewritten JPEG, or null if the data is not a JPEG at all.</returns>
    public static byte[]? SetOrientation(byte[] jpeg, int orientation)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;   // not SOI
        if (orientation is < 1 or > 8) return null;

        // Patching in place is preferred: it touches exactly two bytes.
        if (TryPatchInPlace(jpeg, (ushort)orientation)) return jpeg;

        // No EXIF orientation field to patch, so a minimal APP1 segment carrying one is inserted.
        return InsertExifSegment(jpeg, (ushort)orientation);
    }

    /// <summary>Finds an existing orientation field and overwrites its value.</summary>
    private static bool TryPatchInPlace(byte[] jpeg, ushort orientation)
    {
        if (!TryFindExif(jpeg, out var tiffStart, out var exifEnd)) return false;
        if (!TryReadTiffHeader(jpeg, tiffStart, exifEnd, out var bigEndian, out var ifdOffset)) return false;

        var ifd = tiffStart + ifdOffset;
        if (ifd + 2 > exifEnd) return false;

        var entryCount = ReadUInt16(jpeg, ifd, bigEndian);
        for (var i = 0; i < entryCount; i++)
        {
            // Each IFD entry is 12 bytes: tag, type, count, then an inline value or an offset.
            var entry = ifd + 2 + (i * 12);
            if (entry + 12 > exifEnd) return false;

            if (ReadUInt16(jpeg, entry, bigEndian) != OrientationTag) continue;
            if (ReadUInt16(jpeg, entry + 2, bigEndian) != TypeShort) return false;

            // A single SHORT fits in the value field, so it is stored there rather than at an
            // offset - which is what makes this a two-byte edit.
            WriteUInt16(jpeg, entry + 8, orientation, bigEndian);
            return true;
        }

        return false;
    }

    /// <summary>Locates the TIFF block inside the APP1/Exif segment.</summary>
    private static bool TryFindExif(byte[] jpeg, out int tiffStart, out int exifEnd)
    {
        tiffStart = 0;
        exifEnd = 0;

        var position = 2;   // past SOI
        while (position + 4 <= jpeg.Length)
        {
            if (jpeg[position] != 0xFF) return false;

            var marker = jpeg[position + 1];

            // Start of scan or end of image: no more metadata segments beyond this point.
            if (marker is 0xDA or 0xD9) return false;

            // Standalone markers carry no length field.
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                position += 2;
                continue;
            }

            var length = ReadUInt16(jpeg, position + 2, bigEndian: true);
            if (length < 2 || position + 2 + length > jpeg.Length) return false;

            if (marker == 0xE1 && length >= 8)
            {
                var payload = position + 4;
                if (jpeg[payload] == 'E' && jpeg[payload + 1] == 'x' && jpeg[payload + 2] == 'i' &&
                    jpeg[payload + 3] == 'f' && jpeg[payload + 4] == 0x00 && jpeg[payload + 5] == 0x00)
                {
                    tiffStart = payload + 6;
                    exifEnd = position + 2 + length;
                    return true;
                }
            }

            position += 2 + length;
        }

        return false;
    }

    private static bool TryReadTiffHeader(
        byte[] data, int tiffStart, int limit, out bool bigEndian, out int ifdOffset)
    {
        bigEndian = false;
        ifdOffset = 0;

        if (tiffStart + 8 > limit) return false;

        // "II" little-endian (Intel) or "MM" big-endian (Motorola).
        if (data[tiffStart] == 0x49 && data[tiffStart + 1] == 0x49) bigEndian = false;
        else if (data[tiffStart] == 0x4D && data[tiffStart + 1] == 0x4D) bigEndian = true;
        else return false;

        if (ReadUInt16(data, tiffStart + 2, bigEndian) != 42) return false;

        // The offset is a uint on disk but is bounded by the segment, which is at most 64 KB.
        // Rejecting anything larger keeps the arithmetic in int and guards against a malformed file.
        var raw = ReadUInt32(data, tiffStart + 4, bigEndian);
        if (raw > int.MaxValue) return false;

        ifdOffset = (int)raw;
        return tiffStart + ifdOffset + 2 <= limit;
    }

    /// <summary>
    /// Builds a JPEG carrying a new minimal APP1/Exif segment, for files that have none.
    /// </summary>
    /// <remarks>
    /// The original bytes are copied verbatim either side of the inserted segment, so the image
    /// data is still never re-encoded.
    /// </remarks>
    private static byte[] InsertExifSegment(byte[] jpeg, ushort orientation)
    {
        // TIFF header (8) + entry count (2) + one 12-byte entry + next-IFD offset (4).
        var tiff = new byte[26];
        tiff[0] = 0x49; tiff[1] = 0x49;                                  // little-endian
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);     // IFD0 begins right after
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);     // one entry

        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), OrientationTag);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), TypeShort);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);    // count
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        // Bytes 20-21 are the unused half of the value field; 22-25 are the next-IFD offset (0).

        var identifier = "Exif\0\0"u8;
        var segmentLength = 2 + identifier.Length + tiff.Length;         // length field includes itself

        using var output = new MemoryStream(jpeg.Length + segmentLength + 2);
        output.Write(jpeg, 0, 2);                                        // SOI

        output.WriteByte(0xFF);
        output.WriteByte(0xE1);                                          // APP1
        output.WriteByte((byte)(segmentLength >> 8));
        output.WriteByte((byte)(segmentLength & 0xFF));
        output.Write(identifier);
        output.Write(tiff, 0, tiff.Length);

        // Everything after SOI, byte for byte.
        output.Write(jpeg, 2, jpeg.Length - 2);

        return output.ToArray();
    }

    private static ushort ReadUInt16(byte[] data, int offset, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));

    private static uint ReadUInt32(byte[] data, int offset, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

    private static void WriteUInt16(byte[] data, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
        else BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    }
}
