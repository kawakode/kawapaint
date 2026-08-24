// KawaPaint - image metadata: what a file carries besides its pixels, and getting rid of it.
//
// Worth knowing before reading further: KawaPaint has *never* preserved metadata. Every decode and
// encode goes through the codecs in Codecs/, which hand back a Surface and nothing else, so any
// file this app re-saves already comes out stripped. What was missing was (a) telling the user that,
// and (b) a way to strip a file *without* re-encoding its pixels - re-saving a JPEG to drop a GPS
// tag costs a generation of quality loss for no reason, which is a bad trade for what is usually a
// privacy fix. Everything here is byte-level container surgery: no decode, no re-encode, pixels
// untouched.
//
// Read-only on the metadata itself, deliberately. Parsing EXIF far enough to *report* what is there
// is cheap and safe; writing EXIF back is a much larger job (IFD offset rewriting, MakerNote blobs
// that must be copied verbatim or dropped) and is tracked separately in TODO.md's 2.8.

namespace KawaPaint.Engine.Metadata;

public enum MetadataKind
{
    /// <summary>EXIF/TIFF block - camera settings, timestamps, and GPS coordinates.</summary>
    Exif,

    /// <summary>Adobe XMP packet (XML). Often carries a copy of the EXIF data as well.</summary>
    Xmp,

    /// <summary>IPTC / Photoshop resource block - captions, keywords, credit lines.</summary>
    Iptc,

    /// <summary>ICC colour profile. Removable, but kept by default - dropping it changes how the
    /// image is displayed, which is not what someone asking to "strip metadata" means.</summary>
    IccProfile,

    /// <summary>A free-text comment: JPEG COM, or PNG tEXt/zTXt/iTXt.</summary>
    Comment,

    /// <summary>PNG tIME - last-modification timestamp.</summary>
    Timestamp,

    /// <summary>An application block this code recognises the shape of but not the contents.</summary>
    Other
}

/// <summary>One removable region of the file, located but not interpreted.</summary>
/// <param name="Offset">Start of the region, counted from the first byte of the file, including
/// whatever container framing has to go with it (a JPEG marker, a PNG chunk header + CRC).</param>
/// <param name="Length">Byte count of the whole region, framing included - so removing a block is
/// exactly "copy everything except [Offset, Offset+Length)".</param>
public sealed record MetadataBlock(MetadataKind Kind, string Label, int Offset, int Length);

public sealed class MetadataStripOptions
{
    /// <summary>
    /// Default true. An ICC profile is technically metadata, but it is the one block whose removal
    /// visibly changes the image, so "remove metadata" must not silently take it. Callers that mean
    /// it (a strict "make this file anonymous" mode) can turn it off.
    /// </summary>
    public bool KeepColorProfile { get; set; } = true;

    public static MetadataStripOptions Default => new();
}

/// <summary>What a scan found. Never throws on a malformed file - an unparseable container simply
/// comes back with <see cref="CanStrip"/> false and no blocks.</summary>
public sealed class MetadataReport
{
    /// <summary>Codec id of the container as recognised here ("jpeg", "png", "webp"), or "" when
    /// the bytes matched none of them. Deliberately the same vocabulary as <c>IImageCodec.Id</c>.</summary>
    public string Format { get; init; } = "";

    /// <summary>
    /// True when the container's framing was walked to a deliberate stopping point - JPEG's SOS or
    /// EOI, PNG's IEND, the last RIFF chunk - and a lossless rewrite is therefore safe. False covers
    /// both "not a format this understands" and "started out fine, then hit something malformed or
    /// ran out of bytes"; in either case the honest answer is to refuse rather than to guess, since
    /// the alternative is writing a truncated image over someone's file.
    /// <para>What this does <b>not</b> claim is that the image data is intact. A JPEG's entropy-coded
    /// scan is not marker-structured and is never parsed, so a file truncated after SOS still walks
    /// cleanly. Stripping such a file is still well defined - the surviving bytes are copied through
    /// unchanged - it simply comes out as damaged as it went in.</para>
    /// </summary>
    public bool CanStrip { get; init; }

    public IReadOnlyList<MetadataBlock> Blocks { get; init; } = Array.Empty<MetadataBlock>();

    /// <summary>True when the EXIF block carries a GPS sub-directory. This is the single fact most
    /// people are actually asking about, so it is surfaced on its own rather than left for the
    /// caller to dig out of <see cref="Blocks"/>.</summary>
    public bool HasLocation { get; init; }

    /// <summary>"Make Model" from EXIF, when present - shown so the user can tell that the block
    /// really is their camera's and not something incidental.</summary>
    public string? Camera { get; init; }

    /// <summary>EXIF DateTime, verbatim and unparsed (it is a fixed "YYYY:MM:DD HH:MM:SS" shape,
    /// but a mangled one should be displayed as-is rather than swallowed by a failed parse).</summary>
    public string? Captured { get; init; }

    /// <summary>Blocks this report would hand to a stripper under the given options.</summary>
    public IEnumerable<MetadataBlock> Removable(MetadataStripOptions options)
        => Blocks.Where(b => b.Kind != MetadataKind.IccProfile || !options.KeepColorProfile);

    public bool HasAny => Blocks.Count > 0;

    public int TotalBytes => Blocks.Sum(b => b.Length);

    /// <summary>
    /// A human summary, built here rather than in the dialog so the GUI and any future CLI say the
    /// same thing - the same reasoning as BatchRunner owning the batch vocabulary.
    /// </summary>
    public string Describe(MetadataStripOptions options)
    {
        if (Format.Length == 0)
            return "Not a JPEG, PNG or WebP file - metadata cannot be inspected without decoding it.";
        if (!CanStrip)
            return $"This {Format.ToUpperInvariant()} file could not be read end to end, so it is not safe to rewrite.";
        if (!HasAny)
            return $"No metadata found in this {Format.ToUpperInvariant()} file.";

        var lines = new List<string>();
        foreach (var block in Blocks)
        {
            bool kept = block.Kind == MetadataKind.IccProfile && options.KeepColorProfile;
            lines.Add($"  {(kept ? "keep  " : "remove")}  {block.Label,-28} {FormatSize(block.Length)}");
        }

        if (HasLocation) lines.Insert(0, "  ** contains GPS location **");
        if (Camera is not null) lines.Add($"  camera: {Camera}");
        if (Captured is not null) lines.Add($"  taken:  {Captured}");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Shared by the listing body and the GUI's summary line so the two round alike.</summary>
    public static string FormatSize(int bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
         : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB"
         : $"{bytes} bytes";
}

/// <param name="Bytes">The rewritten file. Reference-equal to the input array when nothing was
/// removed, so a caller can skip the write entirely - see <see cref="Changed"/>.</param>
public sealed record MetadataStripResult(
    byte[] Bytes, IReadOnlyList<MetadataBlock> Removed, int BytesRemoved, bool Changed);
