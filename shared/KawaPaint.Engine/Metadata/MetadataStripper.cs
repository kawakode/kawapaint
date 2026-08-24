// KawaPaint - removes the regions MetadataScanner found, and nothing else.
//
// The rewrite is a splice: copy the file, skipping the reported byte ranges. No decode, no
// re-encode, so the compressed pixel data comes out bit-identical and a JPEG loses no quality -
// which is the entire point of doing this here rather than by round-tripping through the codecs.
//
// WebP is the one container that needs more than a splice, because its VP8X header carries flag
// bits announcing the chunks that were just removed; those are cleared, and the RIFF length is
// corrected. JPEG and PNG need neither: JPEG segments are self-delimiting, and a PNG chunk's CRC
// covers only itself, so removing whole chunks leaves every survivor valid.

namespace KawaPaint.Engine.Metadata;

public static class MetadataStripper
{
    /// <summary>
    /// Rewrites <paramref name="bytes"/> without its metadata. Returns the input array unchanged
    /// (and <c>Changed</c> false) when there is nothing to remove or the container was not
    /// understood - callers should skip writing in that case rather than rewrite a file
    /// byte-for-byte for no reason.
    /// </summary>
    public static MetadataStripResult Strip(byte[] bytes, MetadataStripOptions? options = null)
    {
        options ??= MetadataStripOptions.Default;
        var report = MetadataScanner.Scan(bytes);
        return Strip(bytes, report, options);
    }

    /// <param name="report">A scan of these exact bytes. Passing a report from different bytes
    /// would splice at meaningless offsets, so this overload exists only to let a caller that has
    /// already scanned (to show the user what it found) avoid scanning twice.</param>
    public static MetadataStripResult Strip(byte[] bytes, MetadataReport report, MetadataStripOptions? options = null)
    {
        options ??= MetadataStripOptions.Default;

        if (!report.CanStrip)
            return new MetadataStripResult(bytes, Array.Empty<MetadataBlock>(), 0, false);

        var removing = report.Removable(options).OrderBy(b => b.Offset).ToList();
        if (removing.Count == 0)
            return new MetadataStripResult(bytes, Array.Empty<MetadataBlock>(), 0, false);

        int removed = removing.Sum(b => b.Length);
        var output = new byte[bytes.Length - removed];

        int read = 0, write = 0;
        foreach (var block in removing)
        {
            int keep = block.Offset - read;
            Buffer.BlockCopy(bytes, read, output, write, keep);
            write += keep;
            read = block.Offset + block.Length;
        }
        Buffer.BlockCopy(bytes, read, output, write, bytes.Length - read);

        if (report.Format == "webp") FixWebPContainer(output, removing);

        return new MetadataStripResult(output, removing, removed, true);
    }

    /// <summary>
    /// Strips <paramref name="inputPath"/> into <paramref name="outputPath"/>. The input is read
    /// fully into memory and closed before the output is touched and the write goes through a
    /// temp-file-then-move, so in-place (input == output) is safe and a failure partway through
    /// never leaves a truncated file - the same contract as
    /// <see cref="Scripting.BatchRunner.RunOne"/> and <see cref="DocumentFile.Save(Document,string)"/>.
    /// </summary>
    public static MetadataStripResult StripFile(string inputPath, string outputPath, MetadataStripOptions? options = null)
    {
        byte[] bytes = File.ReadAllBytes(inputPath);
        var result = Strip(bytes, options);

        bool inPlace = string.Equals(
            Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase);

        // Nothing to remove: writing the identical bytes back would only churn the file's timestamp
        // (and, on an in-place run, its position in every backup tool watching the folder).
        if (!result.Changed && inPlace) return result;

        string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) is { Length: > 0 } d ? d : ".";
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, result.Bytes);
            File.Move(temp, outputPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }

        return result;
    }

    // ---- WebP container fixups -------------------------------------------------------------

    private const byte Vp8xIcc = 0x20;
    private const byte Vp8xExif = 0x08;
    private const byte Vp8xXmp = 0x04;

    private static void FixWebPContainer(byte[] output, IReadOnlyList<MetadataBlock> removed)
    {
        // RIFF size counts everything after the first 8 bytes.
        if (output.Length >= 8) WriteLe32(output, 4, (uint)(output.Length - 8));

        // VP8X, when present, is always the first chunk after the 12-byte RIFF/WEBP header, and its
        // flags byte announces which optional chunks follow. Leaving a flag set for a chunk that is
        // no longer there makes the file invalid for strict readers, so clear exactly the bits for
        // what was taken out.
        if (output.Length < 21 || !(output[12] == 'V' && output[13] == 'P' && output[14] == '8' && output[15] == 'X'))
            return;

        byte clear = 0;
        foreach (var block in removed)
        {
            clear |= block.Kind switch
            {
                MetadataKind.Exif => Vp8xExif,
                MetadataKind.Xmp => Vp8xXmp,
                MetadataKind.IccProfile => Vp8xIcc,
                _ => (byte)0
            };
        }

        output[20] &= (byte)~clear;
    }

    private static void WriteLe32(byte[] b, int at, uint value)
    {
        b[at] = (byte)value;
        b[at + 1] = (byte)(value >> 8);
        b[at + 2] = (byte)(value >> 16);
        b[at + 3] = (byte)(value >> 24);
    }
}
