using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace KawaPaint.Engine.Codecs;

/// <summary>Writers for animation containers Skia can decode but does not expose for encoding.</summary>
public static class AnimatedImageEncoder
{
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static void EncodeApng(IReadOnlyList<Surface> frames, IReadOnlyList<int> durationsMs,
        Stream output, bool loop = true)
    {
        ValidateFrames(frames, durationsMs);
        var encoded = frames.Select(frame => Encode(frame, SKEncodedImageFormat.Png)).ToArray();
        List<PngChunk>[] chunks = encoded.Select(ParsePng).ToArray();

        output.Write(PngSignature);
        PngChunk ihdr = chunks[0].First(chunk => chunk.Type == "IHDR");
        WritePngChunk(output, "IHDR", ihdr.Data);
        Span<byte> animationControl = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(animationControl, (uint)frames.Count);
        BinaryPrimitives.WriteUInt32BigEndian(animationControl[4..], loop ? 0u : 1u);
        WritePngChunk(output, "acTL", animationControl);

        uint sequence = 0;
        for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            Span<byte> frameControl = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(frameControl, sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(frameControl[4..], (uint)frames[frameIndex].Width);
            BinaryPrimitives.WriteUInt32BigEndian(frameControl[8..], (uint)frames[frameIndex].Height);
            // x/y offsets remain zero; frames are full-canvas.
            int duration = Math.Clamp(durationsMs[frameIndex], 1, 65535);
            BinaryPrimitives.WriteUInt16BigEndian(frameControl[20..], (ushort)duration);
            BinaryPrimitives.WriteUInt16BigEndian(frameControl[22..], 1000);
            frameControl[24] = 0; // APNG_DISPOSE_OP_NONE
            frameControl[25] = 0; // APNG_BLEND_OP_SOURCE
            WritePngChunk(output, "fcTL", frameControl);

            foreach (PngChunk imageData in chunks[frameIndex].Where(chunk => chunk.Type == "IDAT"))
            {
                if (frameIndex == 0)
                {
                    WritePngChunk(output, "IDAT", imageData.Data);
                }
                else
                {
                    byte[] frameData = new byte[imageData.Data.Length + 4];
                    BinaryPrimitives.WriteUInt32BigEndian(frameData, sequence++);
                    imageData.Data.CopyTo(frameData, 4);
                    WritePngChunk(output, "fdAT", frameData);
                }
            }
        }
        WritePngChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    public static void EncodeWebP(IReadOnlyList<Surface> frames, IReadOnlyList<int> durationsMs,
        Stream output, bool loop = true, int quality = 92)
    {
        ValidateFrames(frames, durationsMs);
        using var body = new MemoryStream();

        Span<byte> extended = stackalloc byte[10];
        extended[0] = 0x02; // animation present
        WriteUInt24(extended[4..], frames[0].Width - 1);
        WriteUInt24(extended[7..], frames[0].Height - 1);
        WriteRiffChunk(body, "VP8X", extended);

        Span<byte> animation = stackalloc byte[6]; // transparent background, loop forever/once
        BinaryPrimitives.WriteUInt16LittleEndian(animation[4..], loop ? (ushort)0 : (ushort)1);
        WriteRiffChunk(body, "ANIM", animation);

        for (int index = 0; index < frames.Count; index++)
        {
            byte[] still = Encode(frames[index], SKEncodedImageFormat.Webp, quality);
            using var framePayload = new MemoryStream();
            Span<byte> header = new byte[16];
            WriteUInt24(header[6..], frames[index].Width - 1);
            WriteUInt24(header[9..], frames[index].Height - 1);
            WriteUInt24(header[12..], Math.Clamp(durationsMs[index], 1, 0xFFFFFF));
            header[15] = 0; // source blend, no disposal
            framePayload.Write(header);
            foreach (RiffChunk chunk in ParseWebP(still))
            {
                if (chunk.Type is not ("ALPH" or "VP8 " or "VP8L")) continue;
                WriteRiffChunk(framePayload, chunk.Type, chunk.Data);
            }
            WriteRiffChunk(body, "ANMF", framePayload.ToArray());
        }

        output.Write("RIFF"u8);
        WriteUInt32Little(output, checked((uint)(body.Length + 4)));
        output.Write("WEBP"u8);
        body.Position = 0;
        body.CopyTo(output);
    }

    private static void ValidateFrames(IReadOnlyList<Surface> frames, IReadOnlyList<int> durationsMs)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(durationsMs);
        if (frames.Count == 0) throw new ArgumentException("At least one frame is required.", nameof(frames));
        if (durationsMs.Count != frames.Count)
            throw new ArgumentException("Every frame needs a duration.", nameof(durationsMs));
        int width = frames[0].Width, height = frames[0].Height;
        if (width > 0x1000000 || height > 0x1000000)
            throw new ArgumentOutOfRangeException(nameof(frames), "Animation dimensions exceed the container limit.");
        if (frames.Any(frame => frame.Width != width || frame.Height != height))
            throw new ArgumentException("All animation frames must have identical dimensions.", nameof(frames));
    }

    private static byte[] Encode(Surface surface, SKEncodedImageFormat format, int quality = 100)
    {
        using var stream = new MemoryStream();
        surface.Encode(stream, format, quality);
        return stream.ToArray();
    }

    private sealed record PngChunk(string Type, byte[] Data);

    private static List<PngChunk> ParsePng(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(PngSignature))
            throw new InvalidDataException("PNG frame encoder returned invalid data.");
        var result = new List<PngChunk>();
        int offset = 8;
        while (offset + 12 <= png.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            if (length < 0 || offset + 12L + length > png.Length) throw new InvalidDataException("Invalid PNG chunk.");
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            result.Add(new PngChunk(type, png.AsSpan(offset + 8, length).ToArray()));
            offset += 12 + length;
            if (type == "IEND") break;
        }
        return result;
    }

    private static void WritePngChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        output.Write(length);
        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        output.Write(typeBytes);
        output.Write(data);
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

    private sealed record RiffChunk(string Type, byte[] Data);

    private static IEnumerable<RiffChunk> ParseWebP(byte[] webp)
    {
        if (webp.Length < 12 || !webp.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !webp.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            throw new InvalidDataException("WebP frame encoder returned invalid data.");
        int offset = 12;
        while (offset + 8 <= webp.Length)
        {
            string type = Encoding.ASCII.GetString(webp, offset, 4);
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(offset + 4, 4)));
            if (length < 0 || offset + 8L + length > webp.Length) throw new InvalidDataException("Invalid WebP chunk.");
            yield return new RiffChunk(type, webp.AsSpan(offset + 8, length).ToArray());
            offset += 8 + length + (length & 1);
        }
    }

    private static void WriteRiffChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        output.Write(Encoding.ASCII.GetBytes(type));
        WriteUInt32Little(output, checked((uint)data.Length));
        output.Write(data);
        if ((data.Length & 1) != 0) output.WriteByte(0);
    }

    private static void WriteUInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    private static void WriteUInt32Little(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }
}
