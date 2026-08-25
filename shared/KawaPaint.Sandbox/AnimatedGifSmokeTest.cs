using KawaPaint.Engine;
using KawaPaint.Engine.Codecs;
using SkiaSharp;

namespace KawaPaint.Sandbox;

internal static class AnimatedGifSmokeTest
{
    public static void RunAll()
    {
        using var first = new Surface(37, 29);
        using var second = new Surface(37, 29);
        first.Clear(ColorBgra.FromBgra(0, 0, 255, 255));
        second.Clear(ColorBgra.FromBgra(255, 0, 0, 255));
        for (int y = 5; y < 15; y++)
        for (int x = 7; x < 20; x++) second[x, y] = ColorBgra.Transparent;

        using var encoded = new MemoryStream();
        AnimatedGifEncoder.Encode(new[] { first, second }, encoded, 120, loop: true);
        byte[] bytes = encoded.ToArray();
        Assert(bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8), "GIF header mismatch");
        Assert(bytes[^1] == 0x3B, "GIF trailer missing");

        using var codecStream = new MemoryStream(bytes);
        using var codec = SKCodec.Create(codecStream)
            ?? throw new InvalidOperationException("Skia refused the encoded GIF");
        Assert(codec.FrameCount == 2, $"GIF frame count was {codec.FrameCount}, expected 2");

        using var decodeStream = new MemoryStream(bytes);
        using var decoded = CodecRegistry.Decode(decodeStream, "animation.gif");
        Assert(decoded.Width == first.Width && decoded.Height == first.Height, "GIF dimensions changed");
        ColorBgra pixel = decoded[0, 0];
        Assert(pixel.R > 220 && pixel.G < 40 && pixel.B < 40, $"first GIF frame decoded incorrectly: {pixel}");

        using var framesStream = new MemoryStream(bytes);
        IReadOnlyList<DecodedImageFrame> decodedFrames = CodecRegistry.DecodeFrames(framesStream, "animation.gif");
        try
        {
            Assert(decodedFrames.Count == 2, $"frame decoder returned {decodedFrames.Count} frames");
            Assert(decodedFrames[0].DurationMs == 120 && decodedFrames[1].DurationMs == 120,
                "GIF frame delays were not preserved");
            ColorBgra secondPixel = decodedFrames[1].Surface[0, 0];
            Assert(secondPixel.B > 220 && secondPixel.R < 40 && secondPixel.G < 40,
                $"second GIF frame decoded incorrectly: {secondPixel}");
            Assert(decodedFrames[1].Surface[8, 6].A < 20,
                "second GIF frame did not apply transparent-background disposal");
        }
        finally
        {
            foreach (DecodedImageFrame frame in decodedFrames) frame.Surface.Dispose();
        }

        using var staticStream = new MemoryStream();
        CodecRegistry.Encode(first, staticStream, "single.gif");
        Assert(staticStream.Length > 800, "static GIF codec produced no palette/image data");

        TestModernAnimationContainers(first, second);
        TestTimelinePersistence(first, second);

        // High-entropy pixels force LZW through 10/11/12-bit codes and dictionary clears.
        using var noise = new Surface(192, 128);
        var random = new Random(0x4B415741);
        for (int y = 0; y < noise.Height; y++)
        for (int x = 0; x < noise.Width; x++)
            noise[x, y] = ColorBgra.FromBgra(
                (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);

        using var noiseStream = new MemoryStream();
        AnimatedGifEncoder.Encode(new[] { noise }, noiseStream);
        noiseStream.Position = 0;
        using var noiseDecoded = Surface.Decode(noiseStream);
        for (int y = 0; y < noise.Height; y += 17)
        for (int x = 0; x < noise.Width; x += 19)
        {
            ColorBgra expected = noise[x, y];
            ColorBgra actual = noiseDecoded[x, y];
            Assert(Math.Abs(expected.R - actual.R) <= 36 && Math.Abs(expected.G - actual.G) <= 36 &&
                   Math.Abs(expected.B - actual.B) <= 85,
                $"high-entropy GIF pixel ({x},{y}) was corrupt: {expected} -> {actual}");
        }
        Console.WriteLine($"ANIMATED GIF SMOKE OK - 2 frames, {bytes.Length} bytes, import/export through Skia");
    }

    private static void TestModernAnimationContainers(Surface first, Surface second)
    {
        Surface[] source = { first, second };
        int[] durations = { 70, 230 };

        using var apng = new MemoryStream();
        AnimatedImageEncoder.EncodeApng(source, durations, apng);
        byte[] pngBytes = apng.ToArray();
        Assert(pngBytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "APNG signature mismatch");
        Assert(System.Text.Encoding.ASCII.GetString(pngBytes).Contains("acTL"), "APNG animation control missing");
        apng.Position = 0;
        IReadOnlyList<DecodedImageFrame> pngFrames = CodecRegistry.DecodeFrames(apng, "animation.png");
        try
        {
            Assert(pngFrames.Count == 2, $"APNG decoded {pngFrames.Count} rather than 2 frames");
            Assert(pngFrames[0].DurationMs == 70 && pngFrames[1].DurationMs == 230, "APNG timings changed");
        }
        finally { foreach (var frame in pngFrames) frame.Surface.Dispose(); }

        if (CodecRegistry.FindById("webp") is { IsAvailable: true })
        {
            using var webp = new MemoryStream();
            AnimatedImageEncoder.EncodeWebP(source, durations, webp);
            byte[] webpBytes = webp.ToArray();
            Assert(webpBytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                   webpBytes.AsSpan(8, 4).SequenceEqual("WEBP"u8), "animated WebP signature mismatch");
            Assert(System.Text.Encoding.ASCII.GetString(webpBytes).Contains("ANIM"), "WebP animation chunk missing");
            webp.Position = 0;
            IReadOnlyList<DecodedImageFrame> webpFrames = CodecRegistry.DecodeFrames(webp, "animation.webp");
            try
            {
                Assert(webpFrames.Count == 2, $"animated WebP decoded {webpFrames.Count} rather than 2 frames");
                Assert(webpFrames[0].DurationMs == 70 && webpFrames[1].DurationMs == 230,
                    "animated WebP timings changed");
            }
            finally { foreach (var frame in webpFrames) frame.Surface.Dispose(); }
        }
    }

    private static void TestTimelinePersistence(Surface first, Surface second)
    {
        using var document = new Document(first.Width, first.Height);
        document.AddLayer(new Layer(first.Clone(), "First layer"));
        document.ExifTiff = new byte[] { 0x49, 0x49, 42, 0, 8, 0, 0, 0 };
        document.ActiveFrame.Name = "Opening";
        document.ActiveFrame.DurationMs = 70;
        document.AddFrame(new DocumentFrame(new[] { new Layer(second.Clone(), "Second layer") }, "Closing", 230));
        using var encoded = new MemoryStream();
        DocumentFile.Save(document, encoded);
        encoded.Position = 0;
        using Document loaded = DocumentFile.Load(encoded);
        Assert(loaded.FrameCount == 2 && loaded.ActiveFrameIndex == 1, "KWP timeline shape was not preserved");
        Assert(loaded.Frames[0].Name == "Opening" && loaded.Frames[0].DurationMs == 70,
            "first KWP frame metadata changed");
        Assert(loaded.Frames[1].Name == "Closing" && loaded.Frames[1].DurationMs == 230,
            "second KWP frame metadata changed");
        Assert(loaded.ExifTiff?.SequenceEqual(document.ExifTiff) == true, "KWP source EXIF was not preserved");
        Assert(loaded.Frames[0].Layers[0].Surface[0, 0].R > 220 &&
               loaded.Frames[1].Layers[0].Surface[0, 0].B > 220, "KWP frame pixels changed");

        using Document resized = DocumentOps.Resize(loaded, 19, 13);
        Assert(resized.FrameCount == 2 && resized.Width == 19 && resized.Height == 13,
            "resize did not transform the complete timeline");
        Assert(resized.Frames[0].DurationMs == 70 && resized.Frames[1].DurationMs == 230,
            "resize changed frame timing");
        using Document flattened = DocumentOps.Flatten(loaded);
        Assert(flattened.FrameCount == 2 && flattened.Frames.All(frame => frame.Layers.Count == 1),
            "flatten did not preserve the frame axis");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
