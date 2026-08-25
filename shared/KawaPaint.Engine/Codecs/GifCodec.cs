// KawaPaint - GIF89a encoder. Animation is exposed separately because IImageCodec's contract is
// intentionally one Surface in / one Surface out; the ordinary codec path writes one valid frame.

using SkiaSharp;

namespace KawaPaint.Engine.Codecs;

public sealed class GifCodec : IFrameImageCodec
{
    public string Id => "gif";
    public string DisplayName => "GIF";
    public IReadOnlyList<string> Extensions { get; } = new[] { ".gif" };
    public bool CanDecode => true;
    public bool CanEncode => true;
    public bool IsAvailable => true;

    public bool MatchesHeader(ReadOnlySpan<byte> header) => header.Length >= 6 &&
        (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8));

    public Surface Decode(Stream stream) => Surface.Decode(stream);

    public unsafe IReadOnlyList<DecodedImageFrame> DecodeFrames(Stream stream)
    {
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidOperationException("could not decode GIF stream");

        SKCodecFrameInfo[] frameInfo = codec.FrameInfo;
        int frameCount = Math.Max(1, codec.FrameCount);
        var frames = new List<DecodedImageFrame>(frameCount);
        var outputInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Bgra8888, SKAlphaType.Unpremul);

        try
        {
            for (int index = 0; index < frameCount; index++)
            {
                var surface = new Surface(outputInfo.Width, outputInfo.Height);
                // Without PriorFrame Skia reconstructs every required predecessor for every frame,
                // turning a long animation into O(frameCount²) work. Seed from a safe decoded frame
                // and let the codec apply blend/disposal from there.
                int priorFrame = FindReusablePriorFrame(index, frameInfo);
                if (priorFrame >= 0) surface.CopyFrom(frames[priorFrame].Surface);
                var options = new SKCodecOptions(index, priorFrame);
                SKCodecResult result = codec.GetPixels(outputInfo, surface.Scan0, options);
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    surface.Dispose();
                    throw new InvalidOperationException($"could not decode GIF frame {index + 1} ({result})");
                }

                int duration = index < frameInfo.Length ? Math.Max(0, frameInfo[index].Duration) : 0;
                frames.Add(new DecodedImageFrame(surface, duration));
            }
            return frames;
        }
        catch
        {
            foreach (DecodedImageFrame frame in frames) frame.Surface.Dispose();
            throw;
        }
    }

    private static int FindReusablePriorFrame(int frameIndex, SKCodecFrameInfo[] info)
    {
        if (frameIndex <= 0 || frameIndex >= info.Length) return -1;
        int required = info[frameIndex].RequiredFrame;
        if (required < 0) return -1;
        for (int candidate = frameIndex - 1; candidate >= required; candidate--)
            if (candidate >= info.Length ||
                info[candidate].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious)
                return candidate;
        return -1;
    }

    public void Encode(Surface surface, Stream stream, EncodeOptions options) =>
        AnimatedGifEncoder.Encode(new[] { surface }, stream);
}

public static class AnimatedGifEncoder
{
    /// <summary>Builds the roadmap's deliberately simple layers-as-frames animation model. Hidden
    /// layers are omitted; each visible layer becomes one standalone full-canvas frame.</summary>
    public static unsafe List<Surface> RenderLayerFrames(Document document)
    {
        var frames = new List<Surface>();
        foreach (Layer layer in document.Layers)
        {
            if (!layer.Visible) continue;
            var frame = new Surface(document.Width, document.Height);
            Surface source = layer.Surface;
            byte opacity = layer.Opacity;
            BlendMode mode = layer.BlendMode;
            Parallel.For(0, document.Height, y =>
            {
                ColorBgra* src = (ColorBgra*)source.GetRowPointer(y);
                ColorBgra* dst = (ColorBgra*)frame.GetRowPointer(y);
                for (int x = 0; x < document.Width; x++)
                    if (src[x].A != 0)
                        dst[x] = Blending.Composite(mode, ColorBgra.Transparent, src[x], opacity);
            });
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary>Composites each real document frame, preserving its independent layer stack.</summary>
    public static unsafe List<Surface> RenderDocumentFrames(Document document)
    {
        var result = new List<Surface>(document.FrameCount);
        foreach (DocumentFrame sourceFrame in document.Frames)
            result.Add(RenderDocumentFrame(document, sourceFrame));
        return result;
    }

    public static unsafe Surface RenderDocumentFrame(Document document, DocumentFrame sourceFrame)
    {
        if (!document.Frames.Contains(sourceFrame))
            throw new ArgumentException("Frame does not belong to this document.", nameof(sourceFrame));
        var surface = new Surface(document.Width, document.Height);
        bool initialized = false;
        foreach (Layer layer in sourceFrame.Layers)
        {
            if (!layer.Visible) continue;
            if (!initialized && layer.BlendMode == BlendMode.Normal && layer.Opacity == 255)
            {
                surface.CopyFrom(layer.Surface);
                initialized = true;
                continue;
            }
            for (int y = 0; y < document.Height; y++)
                Blending.CompositeSpan(layer.BlendMode,
                    (ColorBgra*)surface.GetRowPointer(y),
                    (ColorBgra*)layer.Surface.GetRowPointer(y), document.Width, layer.Opacity);
            initialized = true;
        }
        return surface;
    }

    public static void Encode(IReadOnlyList<Surface> frames, Stream output, int frameDelayMs = 100,
        bool loop = true) => Encode(frames, output, null, frameDelayMs, loop, dither: true);

    public static void Encode(IReadOnlyList<Surface> frames, Stream output,
        IReadOnlyList<int>? frameDurationsMs, int fallbackDelayMs = 100, bool loop = true,
        bool dither = true)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(output);
        if (frames.Count == 0) throw new ArgumentException("At least one frame is required.", nameof(frames));

        int width = frames[0].Width, height = frames[0].Height;
        if (width > ushort.MaxValue || height > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(frames), "GIF dimensions cannot exceed 65535 pixels.");
        foreach (Surface frame in frames)
            if (frame.Width != width || frame.Height != height)
                throw new ArgumentException("All GIF frames must have identical dimensions.", nameof(frames));

        bool hasTransparency = HasTransparency(frames);
        output.Write("GIF89a"u8);
        WriteUInt16(output, width);
        WriteUInt16(output, height);
        output.WriteByte(0xF7); // global 256-color table, 8-bit color resolution
        output.WriteByte(0);    // background index
        output.WriteByte(0);    // square pixels
        GifPalette palette = GifPalette.Build(frames, hasTransparency);
        output.Write(palette.Bytes);

        if (loop && frames.Count > 1) WriteLoopExtension(output);

        byte[] indices = new byte[checked(width * height)];
        for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            Surface frame = frames[frameIndex];
            palette.Quantize(frame, indices, dither);
            int duration = frameDurationsMs is not null && frameIndex < frameDurationsMs.Count
                ? frameDurationsMs[frameIndex] : fallbackDelayMs;
            int delay = Math.Clamp((duration + 5) / 10, 1, ushort.MaxValue);
            WriteGraphicControl(output, delay, hasTransparency);
            WriteImage(output, width, height, indices);
        }
        output.WriteByte(0x3B); // trailer
    }

    private static unsafe bool HasTransparency(IReadOnlyList<Surface> frames)
    {
        foreach (Surface frame in frames)
            for (int y = 0; y < frame.Height; y++)
            {
                ColorBgra* row = (ColorBgra*)frame.GetRowPointer(y);
                for (int x = 0; x < frame.Width; x++)
                    if (row[x].A < 128) return true;
            }
        return false;
    }

    private static void WriteLoopExtension(Stream output)
    {
        output.WriteByte(0x21); output.WriteByte(0xFF); output.WriteByte(11);
        output.Write("NETSCAPE2.0"u8);
        output.WriteByte(3); output.WriteByte(1);
        WriteUInt16(output, 0); // repeat forever
        output.WriteByte(0);
    }

    private static void WriteGraphicControl(Stream output, int delay, bool transparent)
    {
        output.WriteByte(0x21); output.WriteByte(0xF9); output.WriteByte(4);
        // Restore to transparent background between full frames, otherwise transparent pixels in
        // the next layer would incorrectly retain the preceding layer's image.
        output.WriteByte((byte)(transparent ? 0x09 : 0x04));
        WriteUInt16(output, delay);
        output.WriteByte(0);
        output.WriteByte(0);
    }

    private static void WriteImage(Stream output, int width, int height, ReadOnlySpan<byte> indices)
    {
        output.WriteByte(0x2C);
        WriteUInt16(output, 0); WriteUInt16(output, 0);
        WriteUInt16(output, width); WriteUInt16(output, height);
        output.WriteByte(0); // use global palette, non-interlaced
        output.WriteByte(8); // LZW minimum code size

        byte[] compressed = LzwEncode(indices);
        for (int offset = 0; offset < compressed.Length;)
        {
            int count = Math.Min(255, compressed.Length - offset);
            output.WriteByte((byte)count);
            output.Write(compressed, offset, count);
            offset += count;
        }
        output.WriteByte(0);
    }

    private static byte[] LzwEncode(ReadOnlySpan<byte> data)
    {
        const int clearCode = 256, endCode = 257;
        var bytes = new List<byte>(Math.Max(32, data.Length / 2));
        var dictionary = new Dictionary<int, int>(4096);
        int bitBuffer = 0, bitCount = 0;
        int codeSize = 9, nextCode = 258;

        void WriteCode(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                bytes.Add((byte)bitBuffer);
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        WriteCode(clearCode);
        if (data.Length == 0)
        {
            WriteCode(endCode);
            if (bitCount > 0) bytes.Add((byte)bitBuffer);
            return bytes.ToArray();
        }

        int prefix = data[0];
        for (int i = 1; i < data.Length; i++)
        {
            int suffix = data[i];
            int key = (prefix << 8) | suffix;
            if (dictionary.TryGetValue(key, out int combined))
            {
                prefix = combined;
                continue;
            }

            WriteCode(prefix);
            if (nextCode < 4096)
            {
                // The decoder creates a dictionary entry only after it has consumed the next
                // emitted code, so its table is one entry behind the encoder. Emit that boundary
                // code at the old width, then grow before assigning the encoder's next entry.
                if (nextCode == (1 << codeSize) && codeSize < 12) codeSize++;
                dictionary.Add(key, nextCode++);
            }
            else
            {
                WriteCode(clearCode);
                dictionary.Clear();
                codeSize = 9;
                nextCode = 258;
            }
            prefix = suffix;
        }

        WriteCode(prefix);
        WriteCode(endCode);
        if (bitCount > 0) bytes.Add((byte)bitBuffer);
        return bytes.ToArray();
    }

    private static void WriteUInt16(Stream output, int value)
    {
        output.WriteByte((byte)value);
        output.WriteByte((byte)(value >> 8));
    }
}

/// <summary>Global adaptive median-cut palette with optional Floyd-Steinberg error diffusion.</summary>
internal sealed class GifPalette
{
    private readonly ColorBgra[] _colors;
    private readonly bool _transparent;
    private readonly int[] _nearestCache = Enumerable.Repeat(-1, 32 * 32 * 32).ToArray();

    private GifPalette(ColorBgra[] colors, bool transparent)
    {
        _colors = colors;
        _transparent = transparent;
        Bytes = new byte[256 * 3];
        for (int index = 0; index < colors.Length; index++)
        {
            Bytes[index * 3] = colors[index].R;
            Bytes[index * 3 + 1] = colors[index].G;
            Bytes[index * 3 + 2] = colors[index].B;
        }
    }

    public byte[] Bytes { get; }

    private readonly record struct Point(byte R, byte G, byte B, int Count);

    private sealed class Box
    {
        public List<Point> Points { get; }
        public Box(List<Point> points) => Points = points;
        public int Population => Points.Sum(point => point.Count);
        public int RangeR => Points.Max(point => point.R) - Points.Min(point => point.R);
        public int RangeG => Points.Max(point => point.G) - Points.Min(point => point.G);
        public int RangeB => Points.Max(point => point.B) - Points.Min(point => point.B);
        public long Score => Points.Count < 2 ? -1 : (long)Population * Math.Max(RangeR, Math.Max(RangeG, RangeB));
    }

    public static unsafe GifPalette Build(IReadOnlyList<Surface> frames, bool transparent)
    {
        var histogram = new int[32 * 32 * 32];
        long totalPixels = frames.Sum(frame => (long)frame.Width * frame.Height);
        int stride = Math.Max(1, (int)Math.Sqrt(Math.Max(1, totalPixels / 1_000_000.0)));
        foreach (Surface frame in frames)
            for (int y = 0; y < frame.Height; y += stride)
            {
                ColorBgra* row = (ColorBgra*)frame.GetRowPointer(y);
                for (int x = 0; x < frame.Width; x += stride)
                {
                    ColorBgra color = row[x];
                    if (transparent && color.A < 128) continue;
                    histogram[((color.R >> 3) << 10) | ((color.G >> 3) << 5) | (color.B >> 3)]++;
                }
            }

        var points = new List<Point>();
        for (int code = 0; code < histogram.Length; code++)
        {
            int count = histogram[code];
            if (count == 0) continue;
            points.Add(new Point(
                (byte)((((code >> 10) & 31) * 255 + 15) / 31),
                (byte)((((code >> 5) & 31) * 255 + 15) / 31),
                (byte)(((code & 31) * 255 + 15) / 31), count));
        }
        if (points.Count == 0) points.Add(new Point(0, 0, 0, 1));

        int availableColors = transparent ? 255 : 256;
        var boxes = new List<Box> { new(points) };
        while (boxes.Count < availableColors)
        {
            Box? box = boxes.OrderByDescending(item => item.Score).FirstOrDefault();
            if (box is null || box.Score < 0) break;
            int channel = box.RangeR >= box.RangeG && box.RangeR >= box.RangeB ? 0
                : box.RangeG >= box.RangeB ? 1 : 2;
            box.Points.Sort((left, right) => channel switch
            {
                0 => left.R.CompareTo(right.R),
                1 => left.G.CompareTo(right.G),
                _ => left.B.CompareTo(right.B)
            });
            int half = box.Population / 2, cumulative = 0, split = 1;
            for (; split < box.Points.Count; split++)
            {
                cumulative += box.Points[split - 1].Count;
                if (cumulative >= half) break;
            }
            split = Math.Clamp(split, 1, box.Points.Count - 1);
            boxes.Remove(box);
            boxes.Add(new Box(box.Points.GetRange(0, split)));
            boxes.Add(new Box(box.Points.GetRange(split, box.Points.Count - split)));
        }

        var colors = new ColorBgra[256];
        int destination = transparent ? 1 : 0;
        foreach (Box box in boxes)
        {
            long count = box.Population;
            colors[destination++] = ColorBgra.FromBgra(
                (byte)(box.Points.Sum(point => (long)point.B * point.Count) / count),
                (byte)(box.Points.Sum(point => (long)point.G * point.Count) / count),
                (byte)(box.Points.Sum(point => (long)point.R * point.Count) / count), 255);
        }
        ColorBgra padding = colors[Math.Max(transparent ? 1 : 0, destination - 1)];
        while (destination < colors.Length) colors[destination++] = padding;
        return new GifPalette(colors, transparent);
    }

    public unsafe void Quantize(Surface frame, byte[] destination, bool dither)
    {
        int[] current = new int[(frame.Width + 2) * 3];
        int[] next = new int[current.Length];
        int offset = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            ColorBgra* row = (ColorBgra*)frame.GetRowPointer(y);
            Array.Clear(next);
            for (int x = 0; x < frame.Width; x++)
            {
                ColorBgra source = row[x];
                if (_transparent && source.A < 128)
                {
                    destination[offset++] = 0;
                    continue;
                }
                int errorIndex = (x + 1) * 3;
                int red = Math.Clamp(source.R + (dither ? current[errorIndex] / 16 : 0), 0, 255);
                int green = Math.Clamp(source.G + (dither ? current[errorIndex + 1] / 16 : 0), 0, 255);
                int blue = Math.Clamp(source.B + (dither ? current[errorIndex + 2] / 16 : 0), 0, 255);
                int nearest = FindNearest(red, green, blue);
                destination[offset++] = (byte)nearest;
                if (!dither) continue;
                Diffuse(current, errorIndex + 3, red - _colors[nearest].R, green - _colors[nearest].G,
                    blue - _colors[nearest].B, 7);
                Diffuse(next, errorIndex - 3, red - _colors[nearest].R, green - _colors[nearest].G,
                    blue - _colors[nearest].B, 3);
                Diffuse(next, errorIndex, red - _colors[nearest].R, green - _colors[nearest].G,
                    blue - _colors[nearest].B, 5);
                Diffuse(next, errorIndex + 3, red - _colors[nearest].R, green - _colors[nearest].G,
                    blue - _colors[nearest].B, 1);
            }
            (current, next) = (next, current);
        }
    }

    private int FindNearest(int red, int green, int blue)
    {
        int cacheKey = ((red >> 3) << 10) | ((green >> 3) << 5) | (blue >> 3);
        if (_nearestCache[cacheKey] >= 0) return _nearestCache[cacheKey];
        int first = _transparent ? 1 : 0, best = first, bestDistance = int.MaxValue;
        for (int index = first; index < _colors.Length; index++)
        {
            int dr = red - _colors[index].R, dg = green - _colors[index].G, db = blue - _colors[index].B;
            int distance = dr * dr * 2 + dg * dg * 3 + db * db;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = index;
        }
        return _nearestCache[cacheKey] = best;
    }

    private static void Diffuse(int[] errors, int index, int red, int green, int blue, int weight)
    {
        errors[index] += red * weight;
        errors[index + 1] += green * weight;
        errors[index + 2] += blue * weight;
    }
}
