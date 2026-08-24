// KawaPaint - serializable export recipes shared by the GUI and command line.

using KawaPaint.Engine.Codecs;

namespace KawaPaint.Engine.Exporting;

public enum ExportResizeMode
{
    None,
    FitWithin,
    Exact,
    FitAndPad,
    FillAndCrop
}

/// <summary>
/// A named, repeatable export recipe. Paths may be absent: the GUI then asks for a destination,
/// while the CLI accepts --out-dir. FilenamePattern supports {name}, {preset}, {width}, {height},
/// {date}, and {ext}; the extension is repaired to match the selected codec if it is omitted.
/// </summary>
public sealed class ExportPreset
{
    public string CodecId { get; set; } = "png";
    public EncodeOptions EncodeOptions { get; set; } = new();
    public ExportResizeMode ResizeMode { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool AllowUpscale { get; set; }
    public string PaddingColor { get; set; } = "FFFFFFFF";
    public bool Flatten { get; set; } = true;
    public string FilenamePattern { get; set; } = "{name}-{preset}.{ext}";
    public string? OutputFolder { get; set; }
    public string? ScriptPath { get; set; }

    /// <summary>Optional caption/alt-text sidecar. When present, export also writes a .txt file.</summary>
    public string? PackageText { get; set; }

    /// <summary>When true, PackageText is copied to the clipboard by the GUI after export.</summary>
    public bool CopyPackageTextToClipboard { get; set; }
}

