using System.Buffers.Binary;
using System.Text;

namespace KawaPaint.Engine.Metadata;

/// <summary>Extracts a container-neutral TIFF payload and injects it into newly encoded images.</summary>
public static class ExifPreserver
{
    public static byte[]? ExtractTiff(byte[] source)
    {
        MetadataReport report = MetadataScanner.Scan(source);
        MetadataBlock? block = report.Blocks.FirstOrDefault(item => item.Kind == MetadataKind.Exif);
        if (block is null) return null;
        ReadOnlySpan<byte> payload = report.Format switch
        {
            "jpeg" when block.Length >= 10 => source.AsSpan(block.Offset + 10, block.Length - 10),
            "png" when block.Length >= 12 => source.AsSpan(block.Offset + 8, block.Length - 12),
            "webp" when block.Length >= 8 => source.AsSpan(block.Offset + 8,
                BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(block.Offset + 4, 4))),
            _ => ReadOnlySpan<byte>.Empty
        };
        if (payload.StartsWith("Exif\0\0"u8)) payload = payload[6..];
        return payload.Length >= 8 &&
               (payload.StartsWith("II"u8) || payload.StartsWith("MM"u8)) ? payload.ToArray() : null;
    }

    public static byte[] Inject(byte[] encoded, byte[]? tiff, int width, int height)
    {
        if (tiff is not { Length: >= 8 }) return encoded;
        MetadataReport report = MetadataScanner.Scan(encoded);
        return report.Format switch
        {
            "jpeg" => InjectJpeg(encoded, tiff),
            "png" => InjectPng(encoded, tiff),
            "webp" => InjectWebP(encoded, tiff, width, height),
            _ => encoded
        };
    }

    private static byte[] InjectJpeg(byte[] source, byte[] tiff)
    {
        if (tiff.Length + 8 > ushort.MaxValue) return source;
        MetadataReport report = MetadataScanner.Scan(source);
        var remove = report.Blocks.Where(block => block.Kind == MetadataKind.Exif).ToArray();
        using var output = new MemoryStream(source.Length + tiff.Length + 10);
        output.Write(source, 0, 2);
        output.WriteByte(0xFF); output.WriteByte(0xE1);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(tiff.Length + 8));
        output.Write(length); output.Write("Exif\0\0"u8); output.Write(tiff);
        CopyExcluding(source, output, 2, remove);
        return output.ToArray();
    }

    private static byte[] InjectPng(byte[] source, byte[] tiff)
    {
        MetadataReport report = MetadataScanner.Scan(source);
        using var output = new MemoryStream(source.Length + tiff.Length + 12);
        output.Write(source, 0, 8);
        int offset = 8;
        bool injected = false;
        while (offset + 12 <= source.Length)
        {
            int dataLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset, 4)));
            int total = dataLength + 12;
            string type = Encoding.ASCII.GetString(source, offset + 4, 4);
            if (type != "eXIf") output.Write(source, offset, total);
            if (!injected && type == "IHDR") { WritePngChunk(output, "eXIf", tiff); injected = true; }
            offset += total;
        }
        return report.CanStrip && injected ? output.ToArray() : source;
    }

    private static byte[] InjectWebP(byte[] source, byte[] tiff, int width, int height)
    {
        if (source.Length < 12) return source;
        var chunks = new List<(string Type, byte[] Data)>();
        int offset = 12;
        while (offset + 8 <= source.Length)
        {
            string type = Encoding.ASCII.GetString(source, offset, 4);
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset + 4, 4)));
            if (offset + 8L + length > source.Length) return source;
            if (type != "EXIF") chunks.Add((type, source.AsSpan(offset + 8, length).ToArray()));
            offset += 8 + length + (length & 1);
        }
        if (offset != source.Length) return source;

        int extendedIndex = chunks.FindIndex(chunk => chunk.Type == "VP8X");
        if (extendedIndex >= 0)
        {
            byte[] extended = chunks[extendedIndex].Data;
            if (extended.Length < 10) return source;
            extended[0] |= 0x08;
        }
        else
        {
            var extended = new byte[10];
            extended[0] = 0x08;
            WriteUInt24(extended.AsSpan(4), width - 1);
            WriteUInt24(extended.AsSpan(7), height - 1);
            chunks.Insert(0, ("VP8X", extended));
        }
        chunks.Add(("EXIF", tiff));

        using var body = new MemoryStream();
        foreach (var chunk in chunks) WriteRiffChunk(body, chunk.Type, chunk.Data);
        using var output = new MemoryStream();
        output.Write("RIFF"u8);
        WriteUInt32Little(output, checked((uint)(body.Length + 4)));
        output.Write("WEBP"u8);
        body.Position = 0; body.CopyTo(output);
        return output.ToArray();
    }

    private static void CopyExcluding(byte[] source, Stream output, int start,
        IReadOnlyList<MetadataBlock> remove)
    {
        int position = start;
        foreach (MetadataBlock block in remove.OrderBy(block => block.Offset))
        {
            if (block.Offset < position) continue;
            output.Write(source, position, block.Offset - position);
            position = block.Offset + block.Length;
        }
        output.Write(source, position, source.Length - position);
    }

    private static void WritePngChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes); output.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in typeBytes) crc = UpdateCrc(crc, value);
        foreach (byte value in data) crc = UpdateCrc(crc, value);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc);
        output.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }

    private static void WriteRiffChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        output.Write(Encoding.ASCII.GetBytes(type));
        WriteUInt32Little(output, (uint)data.Length);
        output.Write(data);
        if ((data.Length & 1) != 0) output.WriteByte(0);
    }

    private static void WriteUInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value; destination[1] = (byte)(value >> 8); destination[2] = (byte)(value >> 16);
    }

    private static void WriteUInt32Little(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }
}
