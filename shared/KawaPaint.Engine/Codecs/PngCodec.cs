using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace KawaPaint.Engine.Codecs;

/// <summary>PNG codec with APNG frame extraction, compositing, blend and disposal support.</summary>
public sealed class PngCodec : SkiaCodec
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public PngCodec() : base("png", "PNG", SKEncodedImageFormat.Png, new[] { ".png" }, true, Signature) { }

    public override IReadOnlyList<DecodedImageFrame> DecodeFrames(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        List<Chunk> chunks = Parse(bytes);
        if (!chunks.Any(chunk => chunk.Type == "acTL"))
        {
            using var still = new MemoryStream(bytes);
            return new[] { new DecodedImageFrame(Decode(still), 0) };
        }

        Chunk header = chunks.First(chunk => chunk.Type == "IHDR");
        int canvasWidth = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.Data));
        int canvasHeight = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.Data.AsSpan(4)));
        List<Chunk> shared = chunks.Skip(1).TakeWhile(chunk => chunk.Type is not ("IDAT" or "fcTL"))
            .Where(chunk => chunk.Type is not ("acTL" or "fcTL" or "fdAT")).ToList();
        List<FrameData> frames = ExtractFrames(chunks, canvasWidth, canvasHeight);
        var decoded = new List<DecodedImageFrame>(frames.Count);
        using var canvas = new Surface(canvasWidth, canvasHeight);
        try
        {
            foreach (FrameData frame in frames)
            {
                Surface? previous = frame.Dispose == 2 ? canvas.Clone() : null;
                using Surface image = DecodeFrame(header.Data, shared, frame);
                Composite(canvas, image, frame);
                decoded.Add(new DecodedImageFrame(canvas.Clone(), frame.DurationMs));
                if (frame.Dispose == 1) canvas.ClearRect(frame.X, frame.Y, frame.Width, frame.Height, ColorBgra.Transparent);
                else if (frame.Dispose == 2 && previous is not null) canvas.CopyFrom(previous);
                previous?.Dispose();
            }
            return decoded;
        }
        catch
        {
            foreach (DecodedImageFrame frame in decoded) frame.Surface.Dispose();
            throw;
        }
    }

    private sealed record Chunk(string Type, byte[] Data);
    private sealed class FrameData
    {
        public int Width, Height, X, Y, DurationMs;
        public byte Dispose, Blend;
        public List<byte[]> ImageData { get; } = new();
    }

    private static List<FrameData> ExtractFrames(List<Chunk> chunks, int canvasWidth, int canvasHeight)
    {
        var result = new List<FrameData>();
        FrameData? current = null;
        foreach (Chunk chunk in chunks)
        {
            if (chunk.Type == "fcTL")
            {
                if (current is not null) result.Add(current);
                if (chunk.Data.Length != 26) throw new InvalidDataException("Invalid APNG frame control.");
                ushort numerator = BinaryPrimitives.ReadUInt16BigEndian(chunk.Data.AsSpan(20));
                ushort denominator = BinaryPrimitives.ReadUInt16BigEndian(chunk.Data.AsSpan(22));
                if (denominator == 0) denominator = 100;
                current = new FrameData
                {
                    Width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunk.Data.AsSpan(4))),
                    Height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunk.Data.AsSpan(8))),
                    X = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunk.Data.AsSpan(12))),
                    Y = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunk.Data.AsSpan(16))),
                    DurationMs = Math.Max(1, (int)Math.Round(numerator * 1000.0 / denominator)),
                    Dispose = chunk.Data[24], Blend = chunk.Data[25]
                };
            }
            else if (chunk.Type == "IDAT")
            {
                current ??= new FrameData { Width = canvasWidth, Height = canvasHeight, DurationMs = 100 };
                current.ImageData.Add(chunk.Data);
            }
            else if (chunk.Type == "fdAT")
            {
                if (current is null || chunk.Data.Length < 4) throw new InvalidDataException("APNG frame data has no control chunk.");
                current.ImageData.Add(chunk.Data.AsSpan(4).ToArray());
            }
        }
        if (current is not null) result.Add(current);
        if (result.Count == 0 || result.Any(frame => frame.Width <= 0 || frame.Height <= 0 ||
                frame.X < 0 || frame.Y < 0 || frame.X + frame.Width > canvasWidth || frame.Y + frame.Height > canvasHeight ||
                frame.ImageData.Count == 0))
            throw new InvalidDataException("Invalid APNG frame bounds or image data.");
        return result;
    }

    private static Surface DecodeFrame(byte[] originalHeader, List<Chunk> shared, FrameData frame)
    {
        byte[] header = (byte[])originalHeader.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)frame.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)frame.Height);
        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", header);
        foreach (Chunk chunk in shared) WriteChunk(png, chunk.Type, chunk.Data);
        foreach (byte[] data in frame.ImageData) WriteChunk(png, "IDAT", data);
        WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        png.Position = 0;
        return Surface.Decode(png);
    }

    private static void Composite(Surface canvas, Surface image, FrameData frame)
    {
        if (frame.Blend == 0)
            canvas.ClearRect(frame.X, frame.Y, frame.Width, frame.Height, ColorBgra.Transparent);
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
        {
            ColorBgra source = image[x, y];
            int dx = frame.X + x, dy = frame.Y + y;
            canvas[dx, dy] = frame.Blend == 0 ? source :
                Blending.Composite(BlendMode.Normal, canvas[dx, dy], source, 255);
        }
    }

    private static List<Chunk> Parse(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("Invalid PNG signature.");
        var result = new List<Chunk>();
        int offset = 8;
        while (offset + 12 <= png.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            if (length < 0 || offset + 12L + length > png.Length) throw new InvalidDataException("Invalid PNG chunk.");
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            result.Add(new Chunk(type, png.AsSpan(offset + 8, length).ToArray()));
            offset += length + 12;
            if (type == "IEND") break;
        }
        return result;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
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
}
