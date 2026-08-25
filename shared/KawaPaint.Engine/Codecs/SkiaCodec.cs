// KawaPaint - codecs backed by whatever SkiaSharp's native build actually contains.
//
// Note that SKEncodedImageFormat lists formats the shipped native library was not compiled with
// (Jpegxl and Avif among them): the enum member is not proof of a codec. Availability is probed
// by encoding a 1x1 image once and seeing whether Skia hands back data.

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace KawaPaint.Engine.Codecs;

public class SkiaCodec : IFrameImageCodec
{
    private readonly byte[][] _signatures;
    private bool? _available;

    public SkiaCodec(
        string id,
        string displayName,
        SKEncodedImageFormat format,
        IReadOnlyList<string> extensions,
        bool canEncode,
        params byte[][] signatures)
    {
        Id = id;
        DisplayName = displayName;
        Format = format;
        Extensions = extensions;
        CanEncode = canEncode;
        _signatures = signatures;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public SKEncodedImageFormat Format { get; }
    public IReadOnlyList<string> Extensions { get; }

    public virtual bool CanDecode => true;
    public bool CanEncode { get; }

    public virtual bool IsAvailable => _available ??= Probe();

    private bool Probe()
    {
        if (!CanEncode) return true;   // decode-only formats are proven by trying to open a file
        try
        {
            using var bitmap = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(Format, 90);
            return data is { Size: > 0 };
        }
        catch { return false; }
    }

    public virtual bool MatchesHeader(ReadOnlySpan<byte> header)
    {
        foreach (var signature in _signatures)
        {
            if (header.Length < signature.Length) continue;
            if (header[..signature.Length].SequenceEqual(signature)) return true;
        }
        return false;
    }

    public virtual Surface Decode(Stream stream) => Surface.Decode(stream);

    public virtual unsafe IReadOnlyList<DecodedImageFrame> DecodeFrames(Stream stream)
    {
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidOperationException($"could not decode {DisplayName} stream");
        int frameCount = Math.Max(1, codec.FrameCount);
        SKCodecFrameInfo[] frameInfo = codec.FrameInfo;
        var result = new List<DecodedImageFrame>(frameCount);
        var outputInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Bgra8888, SKAlphaType.Unpremul);
        try
        {
            for (int index = 0; index < frameCount; index++)
            {
                var surface = new Surface(outputInfo.Width, outputInfo.Height);
                // Reuse an already composited predecessor. Passing -1 for every frame makes Skia
                // replay the animation from its required frame repeatedly and is quadratic.
                int priorFrame = FindReusablePriorFrame(index, frameInfo);
                if (priorFrame >= 0) surface.CopyFrom(result[priorFrame].Surface);
                SKCodecResult status = codec.GetPixels(outputInfo, surface.Scan0,
                    new SKCodecOptions(index, priorFrame));
                if (status != SKCodecResult.Success && status != SKCodecResult.IncompleteInput)
                {
                    surface.Dispose();
                    throw new InvalidOperationException($"could not decode {DisplayName} frame {index + 1} ({status})");
                }
                int duration = index < frameInfo.Length ? Math.Max(0, frameInfo[index].Duration) : 0;
                result.Add(new DecodedImageFrame(surface, duration));
            }
            return result;
        }
        catch
        {
            foreach (DecodedImageFrame frame in result) frame.Surface.Dispose();
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

    public virtual void Encode(Surface surface, Stream stream, EncodeOptions options)
    {
        if (!CanEncode) throw new CodecUnavailableException(Id, "This format is read-only.");
        if (!IsAvailable) throw new CodecUnavailableException(Id, "SkiaSharp was built without it.");

        int quality = options.Lossless ? 100 : Math.Clamp(options.Quality, 1, 100);
        surface.Encode(stream, Format, quality);
    }
}

/// <summary>WebP carries a RIFF container, so its signature is split across two ranges.</summary>
public sealed class WebPCodec : SkiaCodec
{
    public WebPCodec() : base("webp", "WebP", SKEncodedImageFormat.Webp, new[] { ".webp" }, canEncode: true)
    {
    }

    public override bool MatchesHeader(ReadOnlySpan<byte> header)
        => header.Length >= 12
           && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
           && header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P';
}
