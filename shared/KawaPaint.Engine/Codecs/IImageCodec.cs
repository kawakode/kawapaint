// KawaPaint - image format plug points.
//
// The file dialogs are generated from what is actually registered *and* available at runtime,
// which matters because several formats ship as optional native packs (JPEG 2000, JPEG XL) and
// are simply absent on some platforms, notably the browser build.

using System;
using System.Collections.Generic;
using System.IO;

namespace KawaPaint.Engine.Codecs;

/// <summary>Per-encode knobs. A codec ignores anything it does not understand.</summary>
public sealed class EncodeOptions
{
    /// <summary>1-100 for lossy formats. Ignored when <see cref="Lossless"/> is set.</summary>
    public int Quality { get; set; } = 92;

    /// <summary>Requests lossless encoding where the format supports both modes (WebP, JPEG XL).</summary>
    public bool Lossless { get; set; }

    /// <summary>Icon sizes to emit for container formats such as ICO.</summary>
    public IReadOnlyList<int> IconSizes { get; set; } = new[] { 16, 32, 48, 64, 128, 256 };

    public static EncodeOptions Default => new();
}

public interface IImageCodec
{
    /// <summary>Stable identifier, e.g. "png" or "jpegxl". Used in settings and plugin manifests.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Lower-case, dot-prefixed. The first entry is the default for save dialogs.</summary>
    IReadOnlyList<string> Extensions { get; }

    bool CanDecode { get; }
    bool CanEncode { get; }

    /// <summary>
    /// False when the backing implementation is not present on this platform - an unshipped
    /// native library, or a format Skia was not compiled with. Probed once and cached.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Content sniffing, so a mislabelled file still opens. Header is at least 32 bytes.</summary>
    bool MatchesHeader(ReadOnlySpan<byte> header);

    Surface Decode(Stream stream);

    void Encode(Surface surface, Stream stream, EncodeOptions options);
}

/// <summary>One fully composited frame decoded from an animated image.</summary>
public sealed record DecodedImageFrame(Surface Surface, int DurationMs);

/// <summary>
/// Optional extension for container formats that can expose more than their first frame. Keeping
/// this separate leaves the ordinary single-surface codec contract and third-party codecs intact.
/// </summary>
public interface IFrameImageCodec : IImageCodec
{
    IReadOnlyList<DecodedImageFrame> DecodeFrames(Stream stream);
}

/// <summary>Thrown when a codec is registered but its implementation is unavailable here.</summary>
public sealed class CodecUnavailableException : Exception
{
    public CodecUnavailableException(string codecId, string? detail = null)
        : base($"The {codecId} codec is not available on this platform." +
               (detail is null ? "" : " " + detail))
        => CodecId = codecId;

    public string CodecId { get; }
}
