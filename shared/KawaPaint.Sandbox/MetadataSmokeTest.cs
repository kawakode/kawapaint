// KawaPaint - headless verification of the metadata scanner/stripper against real container bytes.
//
// The EXIF blocks here are built by hand rather than loaded from a fixture file, so the test has no
// binary dependency and still exercises a genuine little-endian TIFF directory - including the GPS
// pointer tag, which is the one flag users actually care about. The pixel checks matter as much as
// the metadata ones: the entire promise of this code is that it does not touch pixels, so every
// case decodes the file before and after and compares the raw bytes.
//
// One real photo is worth more than any amount of synthesis, but a committed test cannot depend on
// one existing. Point KAWAPAINT_TEST_PHOTO at a JPEG/PNG/WebP with metadata to have it checked too;
// without it that case is skipped, matching how the plugin tests treat machine-specific installs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KawaPaint.Engine;
using KawaPaint.Engine.Codecs;
using KawaPaint.Engine.Metadata;
using SkiaSharp;

namespace KawaPaint.Sandbox;

public static class MetadataSmokeTest
{
    public static void RunAll()
    {
        JpegRoundTrip();
        ColorProfilePolicy();
        PngRoundTrip();
        WebPExtendedContainer();
        IsoBoxRoundTrips();
        Jp2QualityMappingIfAvailable();
        TargetedExifEditingPreservesPixelsAndOtherMetadata();
        ExportExifPreservation();
        MalformedInputRefuses();
        RealPhotoIfAvailable();

        Console.WriteLine("METADATA SMOKE OK - jpeg/png/webp/JXL/JP2 round trips, ICC policy, malformed input");
    }

    private static void IsoBoxRoundTrips()
    {
        using Surface pixels = NativePattern(64, 48);
        foreach ((string id, string extension, bool lossless) in new[]
                 { ("jxl", ".jxl", true), ("jp2", ".jp2", false) })
        {
            IImageCodec? codec = CodecRegistry.FindById(id);
            if (codec is not { IsAvailable: true })
            {
                Console.WriteLine($"  (skipping {id} metadata round trip - native codec unavailable)");
                continue;
            }

            byte[] clean = NativeEncode(codec, pixels, new EncodeOptions { Quality = 100, Lossless = lossless });
            MetadataReport cleanReport = MetadataScanner.Scan(clean);
            Expect(cleanReport.Format == id && cleanReport.CanStrip, $"{id} output is not a valid walkable container");

            byte[] tiff = Tiff(gps: true);
            byte[] tagged = ExifPreserver.Inject(clean, tiff, pixels.Width, pixels.Height);
            MetadataReport taggedReport = MetadataScanner.Scan(tagged);
            Expect(taggedReport.HasLocation && taggedReport.Camera == "KawaCam ZX1", $"{id} EXIF was not recognized");
            Expect(ExifPreserver.ExtractTiff(tagged)?.SequenceEqual(tiff) == true,
                $"{id} TIFF payload did not extract intact");
            Expect(taggedReport.Blocks.Count(block => block.Kind == MetadataKind.Exif) == 1,
                $"{id} should contain exactly one EXIF box");
            SameNativePixels(codec, clean, tagged, $"{id} EXIF injection");

            MetadataEditResult edited = MetadataEditor.Edit(tagged, new MetadataEditOptions
            {
                RemoveGps = true,
                CameraModel = "KawaCam ISO-EDITED"
            });
            MetadataReport editedReport = MetadataScanner.Scan(edited.Bytes);
            Expect(edited.Changed && edited.Error is null, $"{id} targeted metadata edit failed: {edited.Error}");
            Expect(!editedReport.HasLocation && editedReport.Camera == "KawaCam ISO-EDITED",
                $"{id} targeted metadata edit did not survive");
            SameNativePixels(codec, clean, edited.Bytes, $"{id} EXIF edit");

            MetadataStripResult stripped = MetadataStripper.Strip(edited.Bytes);
            Expect(stripped.Changed && !MetadataScanner.Scan(stripped.Bytes).HasAny,
                $"{id} metadata strip left a block behind");
            SameNativePixels(codec, clean, stripped.Bytes, $"{id} metadata strip");
        }
    }

    private static void Jp2QualityMappingIfAvailable()
    {
        IImageCodec? codec = CodecRegistry.FindById("jp2");
        if (codec is not { IsAvailable: true }) return;
        using Surface source = NativePattern(96, 64);
        byte[] low = NativeEncode(codec, source, new EncodeOptions { Quality = 20 });
        byte[] high = NativeEncode(codec, source, new EncodeOptions { Quality = 90 });
        using Surface lowDecoded = codec.Decode(new MemoryStream(low));
        using Surface highDecoded = codec.Decode(new MemoryStream(high));
        double lowError = MeanSquaredRgbError(source, lowDecoded);
        double highError = MeanSquaredRgbError(source, highDecoded);
        Expect(highError < lowError,
            $"JP2 quality mapping is reversed or ineffective (Q20 MSE={lowError:F2}, Q90 MSE={highError:F2})");
        Console.WriteLine($"  JP2 fixed-quality calibration: Q20 MSE={lowError:F2}, Q90 MSE={highError:F2}");
    }

    private static void JpegRoundTrip()
    {
        byte[] clean = Encode(SKEncodedImageFormat.Jpeg);
        byte[] tagged = Splice(clean, 2,
            JpegSegment(0xE1, Cat(Ascii("Exif\0\0"), Tiff(gps: true))),
            JpegSegment(0xE1, Cat(Ascii("http://ns.adobe.com/xap/1.0/\0"), Ascii("<x:xmpmeta/>"))),
            JpegSegment(0xED, Cat(Ascii("Photoshop 3.0\0"), new byte[16])),
            JpegSegment(0xFE, Ascii("a comment")));

        var report = MetadataScanner.Scan(tagged);
        Expect(report is { Format: "jpeg", CanStrip: true }, "jpeg should be walkable");
        Expect(report.Blocks.Count == 4, $"expected 4 blocks, got {report.Blocks.Count}");
        Expect(report.HasLocation, "GPS pointer tag should be reported");
        Expect(report.Camera == "KawaCam ZX1", $"camera was '{report.Camera}'");
        Expect(report.Captured == "2026:08:24 12:34:56", $"timestamp was '{report.Captured}'");

        var result = MetadataStripper.Strip(tagged);
        Expect(result.Bytes.SequenceEqual(clean), "stripping should land exactly on the untagged bytes");
        Expect(!MetadataScanner.Scan(result.Bytes).HasAny, "nothing should survive the strip");
        Expect(!MetadataScanner.Scan(result.Bytes).HasLocation, "no location should survive the strip");
        SamePixels(tagged, result.Bytes, "jpeg");

        // Structural application blocks must NOT be treated as metadata: APP0 carries JFIF pixel
        // density and APP14 the Adobe colour transform, and dropping either changes the decode.
        byte[] withAdobe = Splice(clean, 2, JpegSegment(0xEE, Cat(Ascii("Adobe"), new byte[7])), JpegSegment(0xFE, Ascii("x")));
        byte[] strippedAdobe = MetadataStripper.Strip(withAdobe).Bytes;
        Expect(Find(strippedAdobe, Ascii("Adobe")) >= 0, "APP14 Adobe must survive");
        Expect(Find(strippedAdobe, Ascii("JFIF")) >= 0, "APP0 JFIF must survive");

        var untouched = MetadataStripper.Strip(clean);
        Expect(!untouched.Changed && ReferenceEquals(untouched.Bytes, clean),
            "a file with no metadata should come back as the same array, unchanged");
    }

    private static void ColorProfilePolicy()
    {
        byte[] clean = Encode(SKEncodedImageFormat.Jpeg);
        byte[] tagged = Splice(clean, 2,
            JpegSegment(0xE2, Cat(Ascii("ICC_PROFILE\0"), new byte[] { 1, 1 }, new byte[48])),
            JpegSegment(0xE1, Cat(Ascii("Exif\0\0"), Tiff(gps: false))));

        var kept = MetadataScanner.Scan(MetadataStripper.Strip(tagged).Bytes);
        Expect(kept.Blocks.Any(b => b.Kind == MetadataKind.IccProfile), "ICC must survive by default");
        Expect(!kept.Blocks.Any(b => b.Kind == MetadataKind.Exif), "EXIF must not survive by default");

        byte[] all = MetadataStripper.Strip(tagged, new MetadataStripOptions { KeepColorProfile = false }).Bytes;
        Expect(!MetadataScanner.Scan(all).HasAny, "KeepColorProfile=false must remove the ICC block too");
        Expect(all.SequenceEqual(clean), "removing everything should land on the untagged bytes");
    }

    private static void PngRoundTrip()
    {
        byte[] clean = Encode(SKEncodedImageFormat.Png);
        // IHDR is always the first chunk: 8-byte signature + 25-byte chunk.
        byte[] tagged = Splice(clean, 33,
            PngChunk("eXIf", Tiff(gps: true)),
            PngChunk("tEXt", Cat(Ascii("Author\0"), Ascii("somebody"))),
            PngChunk("iTXt", Cat(Ascii("XML:com.adobe.xmp\0"), new byte[] { 0, 0 }, Ascii("\0\0<x:xmpmeta/>"))),
            PngChunk("tIME", new byte[] { 0x07, 0xEA, 8, 24, 12, 0, 0 }));

        var report = MetadataScanner.Scan(tagged);
        Expect(report is { Format: "png", CanStrip: true }, "png should be walkable");
        Expect(report.Blocks.Count == 4, $"expected 4 png blocks, got {report.Blocks.Count}");
        Expect(report.HasLocation, "GPS should be found inside the eXIf chunk");

        byte[] stripped = MetadataStripper.Strip(tagged).Bytes;
        Expect(stripped.SequenceEqual(clean), "png strip should land on the untagged bytes");
        SamePixels(tagged, stripped, "png");
    }

    private static void WebPExtendedContainer()
    {
        byte[] simple = Encode(SKEncodedImageFormat.Webp);
        byte[] tiff = Tiff(gps: true);
        byte[] tagged = Cat(
            Ascii("RIFF"), new byte[4], Ascii("WEBP"),            // length patched below
            RiffChunk("VP8X", Cat(new byte[] { 0x08 | 0x04, 0, 0, 0 }, Uint24(63), Uint24(47))),
            simple[12..],                                          // the codec chunk, verbatim
            RiffChunk("EXIF", tiff),
            RiffChunk("XMP ", Ascii("<x:xmpmeta/>")));
        WriteLe32(tagged, 4, tagged.Length - 8);

        var report = MetadataScanner.Scan(tagged);
        Expect(report is { Format: "webp", CanStrip: true }, "webp should be walkable");
        Expect(report.Blocks.Count == 2, $"expected EXIF + XMP, got {report.Blocks.Count}");
        Expect(report.HasLocation, "GPS should be found inside the EXIF chunk");

        byte[] stripped = MetadataStripper.Strip(tagged).Bytes;
        Expect(ReadLe32(stripped, 4) == (uint)(stripped.Length - 8), "RIFF length must be corrected");
        Expect(stripped[20] == 0, $"VP8X flags must be cleared, were 0x{stripped[20]:X2}");
        Expect(!MetadataScanner.Scan(stripped).HasAny, "nothing should survive the webp strip");
        SamePixels(tagged, stripped, "webp");
    }

    private static void TargetedExifEditingPreservesPixelsAndOtherMetadata()
    {
        byte[] jpegClean = Encode(SKEncodedImageFormat.Jpeg);
        byte[] jpeg = Splice(jpegClean, 2,
            JpegSegment(0xE1, Cat(Ascii("Exif\0\0"), Tiff(gps: true))),
            JpegSegment(0xED, Cat(Ascii("Photoshop 3.0\0"), new byte[16])));
        var request = new MetadataEditOptions
        {
            RemoveGps = true,
            CameraModel = "KawaCam ZX-EDITED LONG",
            Captured = "2027:01:02 03:04:05"
        };
        MetadataEditResult jpegResult = MetadataEditor.Edit(jpeg, request);
        Expect(jpegResult.Changed && jpegResult.Error is null, "jpeg targeted edit failed: " + jpegResult.Error);
        MetadataReport jpegReport = MetadataScanner.Scan(jpegResult.Bytes);
        Expect(!jpegReport.HasLocation, "jpeg GPS pointer survived targeted removal");
        Expect(jpegReport.Camera == "KawaCam ZX-EDITED LONG", $"jpeg camera edit was '{jpegReport.Camera}'");
        Expect(jpegReport.Captured == request.Captured, "jpeg capture date edit did not survive");
        Expect(jpegReport.Blocks.Any(block => block.Kind == MetadataKind.Iptc), "jpeg IPTC was not preserved");
        SamePixels(jpeg, jpegResult.Bytes, "jpeg targeted edit");

        byte[] pngClean = Encode(SKEncodedImageFormat.Png);
        byte[] png = Splice(pngClean, 33, PngChunk("eXIf", Tiff(gps: true)),
            PngChunk("tEXt", Cat(Ascii("Author\0"), Ascii("somebody"))));
        MetadataEditResult pngResult = MetadataEditor.Edit(png, new MetadataEditOptions { RemoveGps = true });
        Expect(pngResult.Changed && !MetadataScanner.Scan(pngResult.Bytes).HasLocation,
            "png GPS-only edit failed");
        Expect(MetadataScanner.Scan(pngResult.Bytes).Blocks.Any(block => block.Kind == MetadataKind.Comment),
            "png text metadata was not preserved");
        SamePixels(png, pngResult.Bytes, "png targeted edit");

        byte[] simple = Encode(SKEncodedImageFormat.Webp);
        byte[] webp = Cat(Ascii("RIFF"), new byte[4], Ascii("WEBP"),
            RiffChunk("VP8X", Cat(new byte[] { 0x08, 0, 0, 0 }, Uint24(63), Uint24(47))),
            simple[12..], RiffChunk("EXIF", Tiff(gps: true)));
        WriteLe32(webp, 4, webp.Length - 8);
        MetadataEditResult webpResult = MetadataEditor.Edit(webp, new MetadataEditOptions { RemoveGps = true });
        Expect(webpResult.Changed && !MetadataScanner.Scan(webpResult.Bytes).HasLocation,
            "webp GPS-only edit failed");
        Expect(ReadLe32(webpResult.Bytes, 4) == webpResult.Bytes.Length - 8, "webp RIFF size was not repaired");
        SamePixels(webp, webpResult.Bytes, "webp targeted edit");
    }

    private static void ExportExifPreservation()
    {
        byte[] tiff = Tiff(gps: true);
        foreach (SKEncodedImageFormat format in new[]
                 { SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Png, SKEncodedImageFormat.Webp })
        {
            byte[] clean = Encode(format);
            byte[] preserved = ExifPreserver.Inject(clean, tiff, 64, 48);
            MetadataReport report = MetadataScanner.Scan(preserved);
            Expect(report.HasLocation && report.Camera == "KawaCam ZX1",
                $"{format} export did not preserve EXIF");
            Expect(ExifPreserver.ExtractTiff(preserved)?.SequenceEqual(tiff) == true,
                $"{format} EXIF did not extract intact");
            SamePixels(clean, preserved, $"{format} EXIF preservation");
        }
    }

    private static void MalformedInputRefuses()
    {
        byte[] exifSegment = JpegSegment(0xE1, Cat(Ascii("Exif\0\0"), Tiff(gps: true)));
        byte[] tagged = Splice(Encode(SKEncodedImageFormat.Jpeg), 2, exifSegment);

        // Cut the file at an exact segment boundary. This is the case that matters and the one a
        // naive walker gets wrong: it runs out of bytes without ever hitting a malformed length, so
        // the loop ends as tidily as a real EOI would and the file looks strippable when it is not.
        byte[] atBoundary = tagged[..(2 + exifSegment.Length)];
        Expect(!MetadataScanner.Scan(atBoundary).CanStrip,
            "a jpeg cut at a segment boundary must not report as strippable");

        byte[] midSegment = tagged[..(2 + exifSegment.Length / 2)];
        Expect(!MetadataScanner.Scan(midSegment).CanStrip,
            "a jpeg cut inside a segment must not report as strippable");

        var refused = MetadataStripper.Strip(atBoundary);
        Expect(!refused.Changed && ReferenceEquals(refused.Bytes, atBoundary),
            "refusing must hand back the original array untouched, never a partial rewrite");

        // Same hole, same shape, in the other two walkers.
        byte[] png = Splice(Encode(SKEncodedImageFormat.Png), 33, PngChunk("tEXt", Ascii("k\0v")));
        Expect(!MetadataScanner.Scan(png[..(33 + 12)]).CanStrip, "a png with no IEND must be refused");

        byte[] webp = Encode(SKEncodedImageFormat.Webp);
        Expect(!MetadataScanner.Scan(webp[..^3]).CanStrip, "a webp with a cut-off chunk must be refused");

        byte[] bogus = (byte[])tagged.Clone();
        bogus[4] = 0x7F; bogus[5] = 0xFF;                          // segment length past end of file
        Expect(!MetadataScanner.Scan(bogus).CanStrip, "an out-of-range segment length must be refused");

        Expect(MetadataScanner.Scan(Ascii("not an image")) is { Format: "", CanStrip: false }, "non-image input");
        Expect(MetadataScanner.Scan(Array.Empty<byte>()) is { Format: "", CanStrip: false }, "empty input");
    }

    private static void RealPhotoIfAvailable()
    {
        string? path = Environment.GetEnvironmentVariable("KAWAPAINT_TEST_PHOTO");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.WriteLine("  (skipping real-photo check - set KAWAPAINT_TEST_PHOTO to a tagged image to enable)");
            return;
        }

        byte[] original = File.ReadAllBytes(path);
        var report = MetadataScanner.Scan(original);
        Expect(report.CanStrip, $"{path} should be walkable");

        var result = MetadataStripper.Strip(original);
        Expect(result.Bytes.Length == original.Length - result.BytesRemoved, "size must drop by exactly what was removed");
        Expect(!MetadataScanner.Scan(result.Bytes).HasAny, "no metadata should survive");
        SamePixels(original, result.Bytes, Path.GetFileName(path));

        Console.WriteLine($"  real photo {Path.GetFileName(path)}: removed {MetadataReport.FormatSize(result.BytesRemoved)} " +
                          $"({string.Join(", ", result.Removed.Select(b => b.Label))}), pixels identical");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static Surface NativePattern(int width, int height)
    {
        var surface = new Surface(width, height);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            surface[x, y] = ColorBgra.FromBgra(
                (byte)((x * 17 + y * 3) & 255), (byte)((x * 5 + y * 19) & 255),
                (byte)((x * 11 + y * 7) & 255), 255);
        return surface;
    }

    private static byte[] NativeEncode(IImageCodec codec, Surface surface, EncodeOptions options)
    {
        using var stream = new MemoryStream();
        codec.Encode(surface, stream, options);
        return stream.ToArray();
    }

    private static void SameNativePixels(IImageCodec codec, byte[] before, byte[] after, string what)
    {
        using Surface a = codec.Decode(new MemoryStream(before));
        using Surface b = codec.Decode(new MemoryStream(after));
        Expect(a.Width == b.Width && a.Height == b.Height, $"{what}: decoded dimensions changed");
        for (int y = 0; y < a.Height; y++)
        for (int x = 0; x < a.Width; x++)
            Expect(a[x, y] == b[x, y], $"{what}: decoded pixel changed at ({x},{y})");
    }

    private static double MeanSquaredRgbError(Surface expected, Surface actual)
    {
        Expect(expected.Width == actual.Width && expected.Height == actual.Height,
            "JP2 quality comparison dimensions changed");
        double sum = 0;
        for (int y = 0; y < expected.Height; y++)
        for (int x = 0; x < expected.Width; x++)
        {
            ColorBgra a = expected[x, y], b = actual[x, y];
            int db = a.B - b.B, dg = a.G - b.G, dr = a.R - b.R;
            sum += db * db + dg * dg + dr * dr;
        }
        return sum / (expected.Width * expected.Height * 3.0);
    }

    private static void SamePixels(byte[] before, byte[] after, string what)
    {
        byte[]? a = Pixels(before), b = Pixels(after);
        Expect(a is not null, $"{what}: the tagged file should decode");
        Expect(b is not null, $"{what}: the stripped file should still decode");
        Expect(a!.SequenceEqual(b!), $"{what}: pixels changed - the strip re-encoded something it shouldn't have");
    }

    private static byte[]? Pixels(byte[] bytes)
    {
        using var bmp = SKBitmap.Decode(bytes);
        using var norm = bmp?.Copy(SKColorType.Bgra8888);
        return norm?.Bytes;
    }

    private static byte[] Encode(SKEncodedImageFormat format)
    {
        using var bmp = new SKBitmap(64, 48);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.OrangeRed, IsAntialias = true };
            canvas.DrawCircle(32, 24, 18, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>A real little-endian TIFF block: IFD0 with Make/Model/DateTime, optionally a GPS
    /// sub-directory pointer. Written out by hand so the reader is parsing genuine structure.</summary>
    private static byte[] Tiff(bool gps)
    {
        const string make = "KawaCam\0", model = "KawaCam ZX1\0", when = "2026:08:24 12:34:56\0";
        int entries = gps ? 4 : 3;
        int dataAt = 8 + 2 + entries * 12 + 4;
        int makeAt = dataAt, modelAt = makeAt + make.Length, whenAt = modelAt + model.Length;
        int gpsAt = whenAt + when.Length;

        var b = new List<byte> { (byte)'I', (byte)'I' };
        Le16(b, 42); Le32(b, 8); Le16(b, entries);
        void Entry(int tag, int type, int count, int value)
        { Le16(b, tag); Le16(b, type); Le32(b, count); Le32(b, value); }

        Entry(0x010F, 2, make.Length, makeAt);
        Entry(0x0110, 2, model.Length, modelAt);
        Entry(0x0132, 2, when.Length, whenAt);
        if (gps) Entry(0x8825, 4, 1, gpsAt);
        Le32(b, 0);

        foreach (string s in new[] { make, model, when }) b.AddRange(Ascii(s));
        if (gps)
        {
            Le16(b, 1);
            Le16(b, 0x0001); Le16(b, 2); Le32(b, 2); Le32(b, 0x0000004E);  // GPSLatitudeRef "N"
            Le32(b, 0);
        }
        return b.ToArray();
    }

    private static byte[] JpegSegment(byte marker, byte[] payload)
    {
        int length = payload.Length + 2;
        return Cat(new[] { (byte)0xFF, marker, (byte)(length >> 8), (byte)length }, payload);
    }

    private static byte[] PngChunk(string type, byte[] data)
    {
        var b = new List<byte>
        {
            (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length
        };
        b.AddRange(Ascii(type));
        b.AddRange(data);
        uint crc = Crc32(Cat(Ascii(type), data));
        b.AddRange(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
        return b.ToArray();
    }

    private static byte[] RiffChunk(string fourcc, byte[] payload)
    {
        var b = new List<byte>();
        b.AddRange(Ascii(fourcc));
        Le32(b, payload.Length);
        b.AddRange(payload);
        if ((payload.Length & 1) != 0) b.Add(0);       // RIFF chunks are padded to an even length
        return b.ToArray();
    }

    private static byte[] Uint24(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16) };

    private static byte[] Splice(byte[] original, int at, params byte[][] inserts)
        => Cat(new[] { original[..at] }.Concat(inserts).Append(original[at..]).ToArray());

    private static byte[] Cat(params byte[][] parts)
    {
        var b = new List<byte>();
        foreach (var p in parts) b.AddRange(p);
        return b.ToArray();
    }

    private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);
    private static void Le16(List<byte> b, int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
    private static void Le32(List<byte> b, long v)
    { b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 24)); }

    private static void WriteLe32(byte[] b, int at, long v)
    { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }

    private static uint ReadLe32(byte[] b, int at)
        => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte x in data)
        {
            crc ^= x;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    private static int Find(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("METADATA SMOKE FAILED: " + message);
    }
}
