// KawaPaint - the set of image formats this build can actually read and write.
//
// File dialogs are generated from here, so a format that failed to load its native library
// simply does not appear rather than failing at save time.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace KawaPaint.Engine.Codecs;

public static class CodecRegistry
{
    private static readonly List<IImageCodec> Codecs = new();

    static CodecRegistry() => RegisterBuiltIns();

    private static void RegisterBuiltIns()
    {
        Register(new PngCodec());

        Register(new SkiaCodec("jpeg", "JPEG", SKEncodedImageFormat.Jpeg, new[] { ".jpg", ".jpeg" }, canEncode: true,
            new byte[] { 0xFF, 0xD8, 0xFF }));

        Register(new WebPCodec());

        Register(new SkiaCodec("bmp", "Bitmap", SKEncodedImageFormat.Bmp, new[] { ".bmp" }, canEncode: false,
            new byte[] { 0x42, 0x4D }));

        Register(new GifCodec());

        Register(new IcoCodec());

        Register(new JxlCodec());
        Register(new Jp2Codec());
    }

    /// <summary>
    /// Adds a codec, replacing any existing one with the same id. Later registrations win so a
    /// plugin can supply a better implementation of a built-in format.
    /// </summary>
    public static void Register(IImageCodec codec)
    {
        Codecs.RemoveAll(c => string.Equals(c.Id, codec.Id, StringComparison.OrdinalIgnoreCase));
        Codecs.Add(codec);
    }

    public static IReadOnlyList<IImageCodec> All => Codecs;

    public static IEnumerable<IImageCodec> Decoders => Codecs.Where(c => c.CanDecode && c.IsAvailable);
    public static IEnumerable<IImageCodec> Encoders => Codecs.Where(c => c.CanEncode && c.IsAvailable);

    public static IImageCodec? FindById(string id)
        => Codecs.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IImageCodec? FindByExtension(string pathOrExtension)
    {
        string ext = pathOrExtension.StartsWith('.')
            ? pathOrExtension.ToLowerInvariant()
            : Path.GetExtension(pathOrExtension).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return null;

        return Codecs.FirstOrDefault(c => c.Extensions.Contains(ext));
    }

    /// <summary>Identifies a format from its leading bytes, for files with a wrong or missing extension.</summary>
    public static IImageCodec? FindByHeader(ReadOnlySpan<byte> header)
    {
        // A plain LINQ predicate can't close over a ref-like span, so this stays a loop.
        foreach (var codec in Codecs)
            if (codec.CanDecode && codec.MatchesHeader(header)) return codec;
        return null;
    }

    /// <summary>
    /// Decodes by content where possible, falling back to the extension and then to Skia's own
    /// detection. Rewinds the stream between attempts, so it must be seekable.
    /// </summary>
    public static Surface Decode(Stream stream, string? fileName = null)
    {
        if (!stream.CanSeek)
        {
            var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            stream = buffered;
        }

        long origin = stream.Position;

        Span<byte> header = stackalloc byte[32];
        int read = stream.Read(header);
        stream.Position = origin;

        var codec = FindByHeader(header[..read]);
        if (codec is null && fileName is not null) codec = FindByExtension(fileName);

        if (codec is { CanDecode: true, IsAvailable: true })
        {
            try { return codec.Decode(stream); }
            catch { stream.Position = origin; }
        }

        return Surface.Decode(stream);
    }

    /// <summary>
    /// Decodes every frame when the identified codec supports animation, otherwise returns the
    /// ordinary decoded image as a one-frame list. The caller owns every returned surface.
    /// </summary>
    public static IReadOnlyList<DecodedImageFrame> DecodeFrames(Stream stream, string? fileName = null)
    {
        if (!stream.CanSeek)
        {
            var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            stream = buffered;
        }

        long origin = stream.Position;
        Span<byte> header = stackalloc byte[32];
        int read = stream.Read(header);
        stream.Position = origin;

        IImageCodec? codec = FindByHeader(header[..read]);
        if (codec is null && fileName is not null) codec = FindByExtension(fileName);
        if (codec is IFrameImageCodec { CanDecode: true, IsAvailable: true } frameCodec)
            return frameCodec.DecodeFrames(stream);

        return new[] { new DecodedImageFrame(Decode(stream, fileName), 0) };
    }

    public static void Encode(Surface surface, Stream stream, string fileName, EncodeOptions? options = null)
    {
        var codec = FindByExtension(fileName)
            ?? throw new CodecUnavailableException(Path.GetExtension(fileName),
                "No codec is registered for this extension.");

        if (!codec.CanEncode) throw new CodecUnavailableException(codec.Id, "This format is read-only.");
        if (!codec.IsAvailable) throw new CodecUnavailableException(codec.Id);

        codec.Encode(surface, stream, options ?? EncodeOptions.Default);
    }
}
