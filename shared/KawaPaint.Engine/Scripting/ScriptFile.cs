// KawaPaint - the .kpscript container: an ordered list of document-level actions (effects, image
// transforms, layer operations) meant to be batch-applied to files, not to reproduce one exact
// recorded session the way a .kpdemo does. That difference is why this is its own small format
// rather than a DemoFile with the pointer opcodes filtered out: a script has no starting document
// to embed, no coordinates, and no more than a few dozen steps, so DemoFile's gzip/varint framing
// (built for tens of thousands of pointer samples) buys nothing here. Plain JSON instead, so a
// script can be hand-read or hand-edited (nudging a blur radius, say) without opening the app.

using System.Text.Json;

namespace KawaPaint.Engine.Scripting;

/// <summary>One recorded step. Args is empty for parameterless ids (image.flipH, layer.add, ...);
/// parametric effect ids (effect.bc, effect.dents, ...) carry the slider values they were
/// committed with.</summary>
public sealed class ScriptStep
{
    public string Id { get; set; } = "";
    public List<double> Args { get; set; } = new();

    public ScriptStep() { }

    public ScriptStep(string id, IReadOnlyList<double>? args = null)
    {
        Id = id;
        if (args is { Count: > 0 }) Args.AddRange(args);
    }
}

public sealed class ScriptFile
{
    public const string Extension = ".kpscript";
    private const int FormatVersion = 1;

    public string Title { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;
    public List<ScriptStep> Steps { get; } = new();

    private sealed class Dto
    {
        public int FormatVersion { get; set; }
        public string Title { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public DateTime RecordedUtc { get; set; }
        public List<ScriptStep> Steps { get; set; } = new();
    }

    /// <summary>
    /// Writes to a temp file beside <paramref name="path"/> and only replaces it once the encode
    /// fully succeeds, mirroring <see cref="DocumentFile.Save(Document,string)"/> - a batch run may
    /// overwrite a script's own source path (unlikely but not worth leaving unguarded), and a
    /// failure mid-write should never leave a truncated .kpscript where a good one used to be.
    /// </summary>
    public void Save(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } d ? d : ".";
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = File.Create(temp)) Save(file);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    public void Save(Stream stream)
    {
        var dto = new Dto
        {
            FormatVersion = FormatVersion,
            Title = Title,
            AppVersion = AppVersion,
            RecordedUtc = RecordedUtc,
            Steps = Steps
        };
        JsonSerializer.Serialize(stream, dto, new JsonSerializerOptions { WriteIndented = true });
    }

    public static ScriptFile Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static ScriptFile Load(Stream stream)
    {
        var dto = JsonSerializer.Deserialize<Dto>(stream)
            ?? throw new InvalidDataException("Not a KawaPaint script file.");
        if (dto.FormatVersion != FormatVersion)
            throw new InvalidDataException(
                $"Script format version {dto.FormatVersion} is not supported by this build (expected {FormatVersion}).");

        var script = new ScriptFile { Title = dto.Title, AppVersion = dto.AppVersion, RecordedUtc = dto.RecordedUtc };
        script.Steps.AddRange(dto.Steps);
        return script;
    }
}
