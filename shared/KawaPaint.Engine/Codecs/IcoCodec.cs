// KawaPaint — Windows icon container.
//
// Skia decodes ICO natively but cannot encode it, so the writer is ours. Every frame is stored
// as PNG, which every consumer since Windows Vista understands and which keeps large frames from
// bloating the file the way an uncompressed DIB would.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KawaPaint.Engine.Codecs;

public sealed class IcoCodec : IImageCodec
{
    private const int MaxIconSize = 256;
    private const int DirectoryEntrySize = 16;
    private const int DirectoryHeaderSize = 6;

    public string Id => "ico";
    public string DisplayName => "Windows Icon";
    public IReadOnlyList<string> Extensions { get; } = new[] { ".ico" };

    public bool CanDecode => true;
    public bool CanEncode => true;
    public bool IsAvailable => true;

    public bool MatchesHeader(ReadOnlySpan<byte> header)
        // reserved=0, type=1 (icon), and at least one image in the directory.
        => header.Length >= 6
           && header[0] == 0 && header[1] == 0
           && header[2] == 1 && header[3] == 0
           && (header[4] | (header[5] << 8)) > 0;

    /// <summary>Decodes the largest frame in the file, which is what Skia's ICO codec selects.</summary>
    public Surface Decode(Stream stream) => Surface.Decode(stream);

    public void Encode(Surface surface, Stream stream, EncodeOptions options)
    {
        var sizes = NormalizeSizes(options.IconSizes);
        if (sizes.Count == 0) throw new ArgumentException("No valid icon sizes requested.", nameof(options));

        // Every frame is encoded up front: the directory has to carry each payload's length and
        // offset, and building it first keeps the writer usable on a non-seekable stream.
        var frames = new List<byte[]>(sizes.Count);
        foreach (int size in sizes) frames.Add(EncodeFrame(surface, size));

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: icon
        writer.Write((ushort)frames.Count);

        int offset = DirectoryHeaderSize + DirectoryEntrySize * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            int size = sizes[i];
            writer.Write((byte)(size >= MaxIconSize ? 0 : size));   // 0 encodes 256
            writer.Write((byte)(size >= MaxIconSize ? 0 : size));
            writer.Write((byte)0);            // palette size: 0 for truecolour
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // colour planes
            writer.Write((ushort)32);         // bits per pixel
            writer.Write((uint)frames[i].Length);
            writer.Write((uint)offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames) writer.Write(frame);
        writer.Flush();
    }

    /// <summary>
    /// Scales the image to fit a square frame, preserving aspect ratio and padding the remainder
    /// with transparency rather than distorting a non-square source.
    /// </summary>
    private static byte[] EncodeFrame(Surface source, int size)
    {
        double scale = Math.Min((double)size / source.Width, (double)size / source.Height);
        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var scaled = source.Resized(w, h);
        using var frame = new Surface(size, size);
        SurfaceOps.ShiftInto(frame, scaled, (size - w) / 2, (size - h) / 2);

        using var buffer = new MemoryStream();
        frame.Encode(buffer, SkiaSharp.SKEncodedImageFormat.Png);
        return buffer.ToArray();
    }

    private static List<int> NormalizeSizes(IReadOnlyList<int> requested)
        => requested
            .Where(s => s is >= 1 and <= MaxIconSize)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
}
