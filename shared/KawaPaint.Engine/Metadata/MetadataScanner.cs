// KawaPaint - locates the metadata regions of a JPEG, PNG or WebP without decoding it.
//
// One walker per container, and both scanning and stripping go through it - MetadataStripper does
// not have a parser of its own, it just splices out the ranges this file reports. That is the whole
// reason the walkers return byte ranges rather than parsed contents: a second parser that drifted
// from this one would mean the dialog telling the user one thing and the rewrite doing another.
//
// Formats not listed here are not a gap to be quietly filled by re-encoding: BMP and ICO have
// nowhere to put metadata, and JPEG XL / JPEG 2000 go through native packs whose containers this
// code has no business rewriting blind. They report Format "" and CanStrip false, and the UI says
// so plainly.

namespace KawaPaint.Engine.Metadata;

public static class MetadataScanner
{
    public static MetadataReport Scan(byte[] bytes) => Scan(bytes.AsSpan());

    public static MetadataReport Scan(ReadOnlySpan<byte> bytes)
    {
        if (StartsWith(bytes, 0xFF, 0xD8, 0xFF)) return ScanJpeg(bytes);
        if (StartsWith(bytes, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return ScanPng(bytes);
        if (bytes.Length >= 12 && Ascii(bytes, 0, "RIFF") && Ascii(bytes, 8, "WEBP")) return ScanWebP(bytes);
        return new MetadataReport();
    }

    public static MetadataReport ScanFile(string path) => Scan(File.ReadAllBytes(path));

    // ---- JPEG ----------------------------------------------------------------------------------

    private static MetadataReport ScanJpeg(ReadOnlySpan<byte> b)
    {
        var blocks = new List<MetadataBlock>();
        var exif = ExifSummary.None;
        int i = 2;

        // Success has to mean "stopped somewhere it meant to stop". Running out of bytes at a
        // segment boundary exits this loop just as cleanly as reaching EOI does, so without this
        // flag a JPEG truncated mid-download would report as walkable and get rewritten.
        bool reachedEnd = false;

        while (i + 1 < b.Length)
        {
            if (b[i] != 0xFF) return Failed("jpeg", blocks);

            // A run of 0xFF before the marker byte is legal padding; the marker is the last one.
            int m = i;
            while (m < b.Length && b[m] == 0xFF) m++;
            if (m >= b.Length) return Failed("jpeg", blocks);

            byte marker = b[m];
            int markerStart = m - 1;

            if (marker == 0xD9) { reachedEnd = true; break; }       // EOI
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i = m + 1; continue; }

            if (m + 2 >= b.Length) return Failed("jpeg", blocks);
            int segLength = (b[m + 1] << 8) | b[m + 2];
            if (segLength < 2) return Failed("jpeg", blocks);

            int payloadAt = m + 3;
            int payloadLength = segLength - 2;
            if (payloadAt + payloadLength > b.Length) return Failed("jpeg", blocks);

            // SOS is followed by entropy-coded scan data, which is not marker-structured in a way
            // worth walking. Nothing removable ever appears after it in practice, so stop here and
            // treat the rest of the file as opaque payload to be copied verbatim.
            if (marker == 0xDA) { reachedEnd = true; break; }

            int total = payloadAt + payloadLength - markerStart;
            var payload = b.Slice(payloadAt, payloadLength);

            switch (marker)
            {
                case 0xE1 when Ascii(payload, 0, "Exif\0\0"):
                    exif = ExifTiffReader.Read(payload[6..]);
                    blocks.Add(new MetadataBlock(MetadataKind.Exif, "EXIF (APP1)", markerStart, total));
                    break;
                case 0xE1 when Ascii(payload, 0, "http://ns.adobe.com/xap/1.0/"):
                    blocks.Add(new MetadataBlock(MetadataKind.Xmp, "XMP (APP1)", markerStart, total));
                    break;
                case 0xE1 when Ascii(payload, 0, "http://ns.adobe.com/xmp/extension/"):
                    blocks.Add(new MetadataBlock(MetadataKind.Xmp, "XMP extension (APP1)", markerStart, total));
                    break;
                case 0xED when Ascii(payload, 0, "Photoshop 3.0\0"):
                    blocks.Add(new MetadataBlock(MetadataKind.Iptc, "IPTC / Photoshop (APP13)", markerStart, total));
                    break;
                case 0xE2 when Ascii(payload, 0, "ICC_PROFILE\0"):
                    blocks.Add(new MetadataBlock(MetadataKind.IccProfile, "ICC colour profile (APP2)", markerStart, total));
                    break;
                case 0xFE:
                    blocks.Add(new MetadataBlock(MetadataKind.Comment, "Comment (COM)", markerStart, total));
                    break;

                // APP0 is JFIF (pixel density - structural, and Document.Dpi round-trips through it)
                // and APP14 is Adobe's colour-transform flag, which decoders need to read YCCK/CMYK
                // JPEGs correctly. Dropping either changes how the file decodes, so neither is
                // metadata for this purpose however much it looks like it.
                case 0xE0:
                case 0xEE:
                    break;

                default:
                    if (marker >= 0xE0 && marker <= 0xEF)
                        blocks.Add(new MetadataBlock(MetadataKind.Other, $"Application block (APP{marker - 0xE0})", markerStart, total));
                    break;
            }

            i = payloadAt + payloadLength;
        }

        return reachedEnd ? Done("jpeg", blocks, exif) : Failed("jpeg", blocks);
    }

    // ---- PNG -----------------------------------------------------------------------------------

    private static MetadataReport ScanPng(ReadOnlySpan<byte> b)
    {
        var blocks = new List<MetadataBlock>();
        var exif = ExifSummary.None;
        int i = 8;
        bool sawIend = false;

        while (i + 8 <= b.Length)
        {
            uint dataLength = Be32(b, i);
            // Chunk framing is 4 length + 4 type + data + 4 CRC.
            if (dataLength > int.MaxValue - 12 || i + 12 + (long)dataLength > b.Length)
                return Failed("png", blocks);

            int total = 12 + (int)dataLength;
            var payload = b.Slice(i + 8, (int)dataLength);

            if (Ascii(b, i + 4, "eXIf"))
            {
                exif = ExifTiffReader.Read(payload);
                blocks.Add(new MetadataBlock(MetadataKind.Exif, "EXIF (eXIf)", i, total));
            }
            else if (Ascii(b, i + 4, "iTXt"))
            {
                // The keyword runs up to the first NUL. XMP is stored under a reserved one.
                bool isXmp = Ascii(payload, 0, "XML:com.adobe.xmp");
                blocks.Add(new MetadataBlock(
                    isXmp ? MetadataKind.Xmp : MetadataKind.Comment,
                    isXmp ? "XMP (iTXt)" : "Text (iTXt)", i, total));
            }
            else if (Ascii(b, i + 4, "tEXt") || Ascii(b, i + 4, "zTXt"))
            {
                blocks.Add(new MetadataBlock(MetadataKind.Comment, "Text (" + AsciiAt(b, i + 4, 4) + ")", i, total));
            }
            else if (Ascii(b, i + 4, "tIME"))
            {
                blocks.Add(new MetadataBlock(MetadataKind.Timestamp, "Modification time (tIME)", i, total));
            }
            else if (Ascii(b, i + 4, "iCCP"))
            {
                blocks.Add(new MetadataBlock(MetadataKind.IccProfile, "ICC colour profile (iCCP)", i, total));
            }

            bool end = Ascii(b, i + 4, "IEND");
            i += total;
            if (end) { sawIend = true; break; }
        }

        // Same rule as the JPEG walker: falling out of the loop for want of bytes is not the same
        // as reaching IEND, and only the second one earns a rewrite.
        if (!sawIend) return Failed("png", blocks);

        return Done("png", blocks, exif);
    }

    // ---- WebP ----------------------------------------------------------------------------------

    private static MetadataReport ScanWebP(ReadOnlySpan<byte> b)
    {
        var blocks = new List<MetadataBlock>();
        var exif = ExifSummary.None;
        int i = 12;

        while (i + 8 <= b.Length)
        {
            uint size = Le32(b, i + 4);
            // RIFF chunks are padded to an even length; the pad byte is not counted in size.
            long padded = size + (size & 1);
            if (padded > int.MaxValue - 8 || i + 8 + padded > b.Length) return Failed("webp", blocks);

            int total = 8 + (int)padded;
            var payload = b.Slice(i + 8, (int)size);

            if (Ascii(b, i, "EXIF"))
            {
                // Some encoders prefix the TIFF block with "Exif\0\0" here even though the WebP
                // spec says the payload is the TIFF block itself. Accept both.
                exif = ExifTiffReader.Read(Ascii(payload, 0, "Exif\0\0") ? payload[6..] : payload);
                blocks.Add(new MetadataBlock(MetadataKind.Exif, "EXIF (EXIF chunk)", i, total));
            }
            else if (Ascii(b, i, "XMP "))
            {
                blocks.Add(new MetadataBlock(MetadataKind.Xmp, "XMP (XMP chunk)", i, total));
            }
            else if (Ascii(b, i, "ICCP"))
            {
                blocks.Add(new MetadataBlock(MetadataKind.IccProfile, "ICC colour profile (ICCP)", i, total));
            }

            i += total;
        }

        // RIFF chunks tile the file exactly. Anything left over is a chunk header that got cut off,
        // which means the same thing here as a missing EOI does for JPEG.
        return i == b.Length ? Done("webp", blocks, exif) : Failed("webp", blocks);
    }

    // ---- shared --------------------------------------------------------------------------------

    private static MetadataReport Done(string format, List<MetadataBlock> blocks, ExifSummary exif)
        => new()
        {
            Format = format,
            CanStrip = true,
            Blocks = blocks,
            HasLocation = exif.HasGps,
            Camera = exif.Camera,
            Captured = exif.DateTime
        };

    /// <summary>A container that started out valid and then didn't. Blocks found so far are kept
    /// for display, but CanStrip is false: rewriting a file this code stopped understanding
    /// halfway through is how you truncate someone's photo.</summary>
    private static MetadataReport Failed(string format, List<MetadataBlock> blocks)
        => new() { Format = format, CanStrip = false, Blocks = blocks };

    private static bool StartsWith(ReadOnlySpan<byte> b, params byte[] prefix)
        => b.Length >= prefix.Length && b[..prefix.Length].SequenceEqual(prefix);

    private static bool Ascii(ReadOnlySpan<byte> b, int at, string text)
    {
        if (at < 0 || at + text.Length > b.Length) return false;
        for (int i = 0; i < text.Length; i++)
            if (b[at + i] != (byte)text[i]) return false;
        return true;
    }

    private static string AsciiAt(ReadOnlySpan<byte> b, int at, int length)
        => at + length > b.Length ? "" : System.Text.Encoding.ASCII.GetString(b.Slice(at, length));

    private static uint Be32(ReadOnlySpan<byte> b, int at)
        => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

    private static uint Le32(ReadOnlySpan<byte> b, int at)
        => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
}
