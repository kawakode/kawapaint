// KawaPaint - targeted EXIF editing without decoding or re-encoding image pixels.

namespace KawaPaint.Engine.Metadata;

public static class MetadataEditor
{
    private const ushort TagMake = 0x010F;
    private const ushort TagModel = 0x0110;
    private const ushort TagDateTime = 0x0132;
    private const ushort TagGpsIfd = 0x8825;
    private const ushort TypeAscii = 2;

    public static MetadataEditResult Edit(byte[] bytes, MetadataEditOptions options)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(options);
        MetadataReport report = MetadataScanner.Scan(bytes);
        if (!report.CanStrip) return new(bytes, false, "The image container cannot be rewritten safely.");
        MetadataBlock? block = report.Blocks.FirstOrDefault(item => item.Kind == MetadataKind.Exif);
        if (block is null) return new(bytes, false, "The file has no EXIF block to edit.");

        if (!TryExtract(bytes, report.Format, block, out byte[] tiff, out bool webpPrefix))
            return new(bytes, false, "The EXIF block is malformed.");
        if (!TryEditTiff(tiff, options, out byte[] edited, out bool changed, out string? error))
            return new(bytes, false, error);
        if (!changed) return new(bytes, false);

        try { return new(Replace(bytes, report.Format, block, edited, webpPrefix), true); }
        catch (Exception ex) { return new(bytes, false, ex.Message); }
    }

    private static bool TryExtract(byte[] bytes, string format, MetadataBlock block,
        out byte[] tiff, out bool webpPrefix)
    {
        tiff = Array.Empty<byte>();
        webpPrefix = false;
        int start, length;
        switch (format)
        {
            case "jpeg":
                start = block.Offset + 10; // marker + length + Exif\0\0
                length = block.Length - 10;
                break;
            case "png":
                start = block.Offset + 8;
                length = block.Length - 12;
                break;
            case "webp":
                start = block.Offset + 8;
                length = (int)ReadLe32(bytes, block.Offset + 4);
                webpPrefix = length >= 6 && Ascii(bytes, start, "Exif\0\0");
                if (webpPrefix) { start += 6; length -= 6; }
                break;
            case "jxl":
                if (!IsoBmffBoxes.TryRead(bytes, block.Offset, out var jxl) || jxl.PayloadLength < 4)
                    return false;
                uint tiffOffset = ReadBe32(bytes, jxl.PayloadOffset);
                if (tiffOffset > int.MaxValue) return false;
                start = checked(jxl.PayloadOffset + 4 + (int)tiffOffset);
                length = jxl.End - start;
                break;
            case "jp2":
                if (!IsoBmffBoxes.TryRead(bytes, block.Offset, out var jp2) || jp2.PayloadLength < 24)
                    return false;
                start = jp2.PayloadOffset + 16;
                length = jp2.PayloadLength - 16;
                break;
            default:
                return false;
        }
        if (start < 0 || length < 8 || start + length > bytes.Length) return false;
        tiff = bytes.AsSpan(start, length).ToArray();
        return true;
    }

    private static bool TryEditTiff(byte[] original, MetadataEditOptions options,
        out byte[] edited, out bool changed, out string? error)
    {
        edited = original.ToArray(); changed = false; error = null;
        if (edited.Length < 8) { error = "The TIFF header is truncated."; return false; }
        bool little;
        if (edited[0] == 'I' && edited[1] == 'I') little = true;
        else if (edited[0] == 'M' && edited[1] == 'M') little = false;
        else { error = "The TIFF byte order is invalid."; return false; }
        if (ReadU16(edited, 2, little) != 42) { error = "The TIFF signature is invalid."; return false; }
        int ifd = checked((int)ReadU32(edited, 4, little));
        if (ifd < 8 || ifd + 2 > edited.Length) { error = "The EXIF IFD is out of range."; return false; }
        int count = ReadU16(edited, ifd, little);
        int entries = ifd + 2;
        if (entries + (long)count * 12 + 4 > edited.Length)
        { error = "The EXIF IFD is truncated."; return false; }

        if (options.RemoveGps)
        {
            int gpsIndex = FindEntry(edited, entries, count, little, TagGpsIfd);
            if (gpsIndex >= 0)
            {
                int entryAt = entries + gpsIndex * 12;
                int gpsOffset = checked((int)ReadU32(edited, entryAt + 8, little));
                ZeroIfd(edited, gpsOffset, little);

                int bytesAfter = (count - gpsIndex - 1) * 12 + 4; // entries plus next-IFD pointer
                Buffer.BlockCopy(edited, entryAt + 12, edited, entryAt, bytesAfter);
                Array.Clear(edited, entryAt + bytesAfter, 12);
                WriteU16(edited, ifd, (ushort)(count - 1), little);
                count--;
                changed = true;
            }
        }

        foreach (var (tag, value, label) in new[]
        {
            (TagMake, options.CameraMake, "camera make"),
            (TagModel, options.CameraModel, "camera model"),
            (TagDateTime, options.Captured, "capture date")
        })
        {
            if (value is null) continue;
            int index = FindEntry(edited, entries, count, little, tag);
            if (index < 0)
            {
                error = $"Cannot add {label}: this EXIF block has no existing tag for it.";
                return false;
            }
            WriteAscii(ref edited, entries + index * 12, value, little);
            changed = true;
        }
        return true;
    }

    private static int FindEntry(byte[] bytes, int entries, int count, bool little, ushort tag)
    {
        for (int index = 0; index < count; index++)
            if (ReadU16(bytes, entries + index * 12, little) == tag) return index;
        return -1;
    }

    private static void WriteAscii(ref byte[] tiff, int entry, string value, bool little)
    {
        byte[] encoded = System.Text.Encoding.Latin1.GetBytes(value + "\0");
        WriteU16(tiff, entry + 2, TypeAscii, little);
        WriteU32(tiff, entry + 4, (uint)encoded.Length, little);
        if (encoded.Length <= 4)
        {
            Array.Clear(tiff, entry + 8, 4);
            encoded.CopyTo(tiff, entry + 8);
            return;
        }

        int offset = tiff.Length;
        Array.Resize(ref tiff, checked(offset + encoded.Length + (encoded.Length & 1)));
        encoded.CopyTo(tiff, offset);
        WriteU32(tiff, entry + 8, (uint)offset, little);
    }

    private static void ZeroIfd(byte[] tiff, int offset, bool little)
    {
        if (offset < 0 || offset + 2 > tiff.Length) return;
        int count = ReadU16(tiff, offset, little);
        int entries = offset + 2;
        if (entries + (long)count * 12 + 4 > tiff.Length) return;
        for (int index = 0; index < count; index++)
        {
            int entry = entries + index * 12;
            ushort type = ReadU16(tiff, entry + 2, little);
            uint itemCount = ReadU32(tiff, entry + 4, little);
            int typeSize = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 => 8, _ => 0 };
            long byteCount = (long)itemCount * typeSize;
            if (byteCount > 4 && byteCount <= int.MaxValue)
            {
                int valueOffset = checked((int)ReadU32(tiff, entry + 8, little));
                if (valueOffset >= 0 && valueOffset + byteCount <= tiff.Length)
                    Array.Clear(tiff, valueOffset, (int)byteCount);
            }
        }
        Array.Clear(tiff, offset, 2 + count * 12 + 4);
    }

    private static byte[] Replace(byte[] source, string format, MetadataBlock block,
        byte[] tiff, bool webpPrefix)
    {
        return format switch
        {
            "jpeg" => ReplaceJpeg(source, block, tiff),
            "png" => ReplacePng(source, block, tiff),
            "webp" => ReplaceWebP(source, block, tiff, webpPrefix),
            "jxl" => ReplaceIsoBox(source, block, "Exif", PrefixJxlExif(tiff)),
            "jp2" => ReplaceIsoBox(source, block, "uuid", PrefixJp2Exif(tiff)),
            _ => source
        };
    }

    private static byte[] PrefixJxlExif(byte[] tiff)
    {
        byte[] payload = new byte[checked(4 + tiff.Length)];
        tiff.CopyTo(payload, 4);
        return payload;
    }

    private static readonly byte[] Jp2ExifUuid =
        { 0x4A, 0x70, 0x67, 0x54, 0x69, 0x66, 0x66, 0x45, 0x78, 0x69, 0x66, 0x2D, 0x3E, 0x4A, 0x50, 0x32 };

    private static byte[] PrefixJp2Exif(byte[] tiff)
    {
        byte[] payload = new byte[checked(16 + tiff.Length)];
        Jp2ExifUuid.CopyTo(payload, 0);
        tiff.CopyTo(payload, 16);
        return payload;
    }

    private static byte[] ReplaceIsoBox(byte[] source, MetadataBlock block, string type, byte[] payload)
        => ReplaceRange(source, block.Offset, block.Length, IsoBmffBoxes.BoxBytes(type, payload));

    private static byte[] ReplaceJpeg(byte[] source, MetadataBlock block, byte[] tiff)
    {
        int payloadLength = checked(6 + tiff.Length);
        if (payloadLength + 2 > ushort.MaxValue) throw new InvalidDataException("Edited EXIF exceeds JPEG APP1's 64 KB limit.");
        byte[] segment = new byte[checked(4 + payloadLength)];
        segment[0] = 0xFF; segment[1] = 0xE1;
        segment[2] = (byte)((payloadLength + 2) >> 8); segment[3] = (byte)(payloadLength + 2);
        "Exif\0\0"u8.CopyTo(segment.AsSpan(4));
        tiff.CopyTo(segment, 10);
        return ReplaceRange(source, block.Offset, block.Length, segment);
    }

    private static byte[] ReplacePng(byte[] source, MetadataBlock block, byte[] tiff)
    {
        byte[] chunk = new byte[checked(12 + tiff.Length)];
        WriteBe32(chunk, 0, (uint)tiff.Length);
        "eXIf"u8.CopyTo(chunk.AsSpan(4));
        tiff.CopyTo(chunk, 8);
        WriteBe32(chunk, 8 + tiff.Length, Crc32(chunk.AsSpan(4, 4 + tiff.Length)));
        return ReplaceRange(source, block.Offset, block.Length, chunk);
    }

    private static byte[] ReplaceWebP(byte[] source, MetadataBlock block, byte[] tiff, bool prefix)
    {
        int payloadLength = tiff.Length + (prefix ? 6 : 0);
        byte[] chunk = new byte[checked(8 + payloadLength + (payloadLength & 1))];
        "EXIF"u8.CopyTo(chunk);
        WriteLe32(chunk, 4, (uint)payloadLength);
        int at = 8;
        if (prefix) { "Exif\0\0"u8.CopyTo(chunk.AsSpan(at)); at += 6; }
        tiff.CopyTo(chunk, at);
        byte[] output = ReplaceRange(source, block.Offset, block.Length, chunk);
        WriteLe32(output, 4, (uint)(output.Length - 8));
        return output;
    }

    private static byte[] ReplaceRange(byte[] source, int offset, int length, byte[] replacement)
    {
        byte[] output = new byte[checked(source.Length - length + replacement.Length)];
        Buffer.BlockCopy(source, 0, output, 0, offset);
        Buffer.BlockCopy(replacement, 0, output, offset, replacement.Length);
        Buffer.BlockCopy(source, offset + length, output, offset + replacement.Length,
            source.Length - offset - length);
        return output;
    }

    private static ushort ReadU16(byte[] b, int at, bool little) => little
        ? (ushort)(b[at] | b[at + 1] << 8) : (ushort)(b[at] << 8 | b[at + 1]);
    private static uint ReadU32(byte[] b, int at, bool little) => little ? ReadLe32(b, at)
        : (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
    private static uint ReadLe32(byte[] b, int at) =>
        (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);
    private static uint ReadBe32(byte[] b, int at) =>
        (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
    private static void WriteU16(byte[] b, int at, ushort value, bool little)
    { if (little) { b[at] = (byte)value; b[at + 1] = (byte)(value >> 8); } else { b[at] = (byte)(value >> 8); b[at + 1] = (byte)value; } }
    private static void WriteU32(byte[] b, int at, uint value, bool little)
    { if (little) WriteLe32(b, at, value); else WriteBe32(b, at, value); }
    private static void WriteLe32(byte[] b, int at, uint value)
    { b[at] = (byte)value; b[at + 1] = (byte)(value >> 8); b[at + 2] = (byte)(value >> 16); b[at + 3] = (byte)(value >> 24); }
    private static void WriteBe32(byte[] b, int at, uint value)
    { b[at] = (byte)(value >> 24); b[at + 1] = (byte)(value >> 16); b[at + 2] = (byte)(value >> 8); b[at + 3] = (byte)value; }
    private static bool Ascii(byte[] b, int at, string value)
    { if (at < 0 || at + value.Length > b.Length) return false; for (int i = 0; i < value.Length; i++) if (b[at + i] != (byte)value[i]) return false; return true; }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
