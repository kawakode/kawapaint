// KawaPaint - just enough TIFF/EXIF to say what is in an EXIF block, never enough to write one.
//
// Scope is deliberately one directory deep: IFD0 only, no recursion into the Exif or GPS
// sub-directories. That is not laziness - IFD0 is where the GPS pointer tag lives, which is the
// fact that matters here, and refusing to follow offsets means a hostile or corrupt file cannot
// walk this code into a loop or a long chain of reads. Every read is bounds-checked and every
// failure degrades to "field absent" rather than an exception, because this runs while merely
// *listing* a file the user picked, and a bad file must not take the dialog down with it.

namespace KawaPaint.Engine.Metadata;

internal readonly record struct ExifSummary(bool HasGps, string? Camera, string? DateTime)
{
    public static ExifSummary None => default;
}

internal static class ExifTiffReader
{
    private const ushort TagMake = 0x010F;
    private const ushort TagModel = 0x0110;
    private const ushort TagDateTime = 0x0132;
    private const ushort TagGpsIfd = 0x8825;
    private const ushort TypeAscii = 2;

    /// <param name="tiff">The TIFF block itself - i.e. starting at the "II"/"MM" byte order mark,
    /// with the JPEG "Exif\0\0" prefix already skipped by the caller. All offsets inside a TIFF
    /// block are relative to this position, which is exactly why it is passed sliced.</param>
    public static ExifSummary Read(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return ExifSummary.None;

        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return ExifSummary.None;

        if (U16(tiff, 2, little) != 42) return ExifSummary.None;

        uint ifd0 = U32(tiff, 4, little);
        if (ifd0 < 8 || ifd0 + 2 > (uint)tiff.Length) return ExifSummary.None;

        int entryCount = U16(tiff, (int)ifd0, little);
        int entriesAt = (int)ifd0 + 2;

        // 12 bytes per entry; a count that would run past the end means the block is malformed, so
        // clamp rather than trust it - a truncated EXIF is common in the wild and still worth
        // reporting the readable part of.
        int maxEntries = (tiff.Length - entriesAt) / 12;
        if (maxEntries <= 0) return ExifSummary.None;
        if (entryCount > maxEntries) entryCount = maxEntries;

        bool hasGps = false;
        string? make = null, model = null, dateTime = null;

        for (int i = 0; i < entryCount; i++)
        {
            int at = entriesAt + i * 12;
            ushort tag = U16(tiff, at, little);
            ushort type = U16(tiff, at + 2, little);
            uint count = U32(tiff, at + 4, little);

            switch (tag)
            {
                case TagGpsIfd:
                    hasGps = true;
                    break;
                case TagMake:
                    make = ReadAscii(tiff, at + 8, type, count, little);
                    break;
                case TagModel:
                    model = ReadAscii(tiff, at + 8, type, count, little);
                    break;
                case TagDateTime:
                    dateTime = ReadAscii(tiff, at + 8, type, count, little);
                    break;
            }
        }

        string? camera = (make, model) switch
        {
            (null, null) => null,
            (null, var m) => m,
            (var mk, null) => mk,
            // Most cameras repeat the maker inside the model ("NIKON CORPORATION" / "NIKON D750"),
            // so joining them blindly reads badly. Drop the maker when the model already says it.
            var (mk, m) => m!.StartsWith(mk!.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? m : mk + " " + m
        };

        return new ExifSummary(hasGps, camera, dateTime);
    }

    /// <param name="valueAt">Offset of the entry's 4-byte value field. Values of 4 bytes or fewer
    /// live inline there; anything longer stores an offset into the TIFF block instead.</param>
    private static string? ReadAscii(ReadOnlySpan<byte> tiff, int valueAt, ushort type, uint count, bool little)
    {
        if (type != TypeAscii || count == 0 || count > 256) return null;

        int start;
        if (count <= 4)
        {
            start = valueAt;
        }
        else
        {
            uint offset = U32(tiff, valueAt, little);
            if (offset + count > (uint)tiff.Length) return null;
            start = (int)offset;
        }

        if (start + (int)count > tiff.Length) return null;

        var span = tiff.Slice(start, (int)count);
        int nul = span.IndexOf((byte)0);
        if (nul >= 0) span = span[..nul];

        // ASCII by spec, but plenty of real files put a local codepage in here. Latin-1 keeps those
        // bytes displayable instead of turning them into replacement characters.
        var text = new string(System.Text.Encoding.Latin1.GetString(span).Trim());
        return text.Length == 0 ? null : text;
    }

    private static ushort U16(ReadOnlySpan<byte> b, int at, bool little)
    {
        if (at + 2 > b.Length) return 0;
        return little ? (ushort)(b[at] | (b[at + 1] << 8)) : (ushort)((b[at] << 8) | b[at + 1]);
    }

    private static uint U32(ReadOnlySpan<byte> b, int at, bool little)
    {
        if (at + 4 > b.Length) return 0;
        return little
            ? (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24))
            : (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);
    }
}
