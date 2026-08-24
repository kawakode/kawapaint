// KawaPaint - one export-preset implementation for GUI, CLI and smoke tests.

using System.Globalization;
using System.Text;
using KawaPaint.Engine.Codecs;
using KawaPaint.Engine.Scripting;

namespace KawaPaint.Engine.Exporting;

public sealed record PresetExportResult(
    string OutputPath, string? SidecarPath, int Width, int Height,
    IReadOnlyList<ScriptStepResult> ScriptSteps);

public static class PresetExporter
{
    public static PresetExportResult ExportInputFile(string inputPath, string presetName,
        ExportPreset preset, string? outputFolderOverride = null, string? scriptBaseDirectory = null)
    {
        using Document source = LoadDocument(inputPath);
        return ExportFile(source, Path.GetFileName(inputPath), presetName, preset,
            outputFolderOverride, scriptBaseDirectory);
    }

    public static PresetExportResult ExportFile(Document source, string sourceName, string presetName,
        ExportPreset preset, string? outputFolderOverride = null, string? scriptBaseDirectory = null)
    {
        Validate(preset);
        string folder = outputFolderOverride ?? preset.OutputFolder
            ?? throw new InvalidOperationException("This preset has no output folder. Supply one before exporting.");

        Directory.CreateDirectory(folder);
        using Document prepared = Prepare(source, preset, scriptBaseDirectory, out var steps);
        string fileName = GetOutputFileName(sourceName, presetName, preset, prepared.Width, prepared.Height);
        string outputPath = Path.Combine(folder, fileName);
        string? sidecarPath = string.IsNullOrWhiteSpace(preset.PackageText)
            ? null
            : Path.ChangeExtension(outputPath, ".txt");

        SaveAtomic(prepared, outputPath, preset);
        if (sidecarPath is not null)
            WriteTextAtomic(sidecarPath, ExpandText(preset.PackageText!, sourceName, presetName,
                prepared.Width, prepared.Height));

        return new PresetExportResult(outputPath, sidecarPath, prepared.Width, prepared.Height, steps);
    }

    /// <summary>Builds the exact document that will be encoded. The caller owns the result.</summary>
    public static Document Prepare(Document source, ExportPreset preset, string? scriptBaseDirectory,
        out IReadOnlyList<ScriptStepResult> scriptSteps)
    {
        Validate(preset);
        Document working = source.Clone();
        try
        {
            scriptSteps = Array.Empty<ScriptStepResult>();
            if (!string.IsNullOrWhiteSpace(preset.ScriptPath))
            {
                string scriptPath = preset.ScriptPath!;
                if (!Path.IsPathRooted(scriptPath) && !string.IsNullOrEmpty(scriptBaseDirectory))
                    scriptPath = Path.Combine(scriptBaseDirectory, scriptPath);
                var script = ScriptFile.Load(scriptPath);
                scriptSteps = ScriptExecutor.Run(ref working, script);
            }

            if (preset.Flatten && working.LayerCount > 1)
                Replace(ref working, DocumentOps.Flatten(working));

            ApplyResize(ref working, preset);
            return working;
        }
        catch
        {
            working.Dispose();
            throw;
        }
    }

    public static string GetOutputFileName(string sourceName, string presetName, ExportPreset preset,
        int width, int height)
    {
        var codec = RequireCodec(preset.CodecId);
        string ext = codec.Extensions[0].TrimStart('.');
        string baseName = Path.GetFileNameWithoutExtension(sourceName);
        string pattern = string.IsNullOrWhiteSpace(preset.FilenamePattern)
            ? "{name}-{preset}.{ext}"
            : preset.FilenamePattern;
        string expanded = pattern
            .Replace("{name}", baseName, StringComparison.OrdinalIgnoreCase)
            .Replace("{preset}", presetName, StringComparison.OrdinalIgnoreCase)
            .Replace("{width}", width.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{height}", height.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{ext}", ext, StringComparison.OrdinalIgnoreCase);

        // A preset is allowed to name a file, never escape its chosen output directory.
        expanded = Path.GetFileName(expanded.Trim());
        foreach (char invalid in Path.GetInvalidFileNameChars()) expanded = expanded.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(expanded)) expanded = baseName + "." + ext;

        string wantedExtension = "." + ext;
        if (!codec.Extensions.Contains(Path.GetExtension(expanded), StringComparer.OrdinalIgnoreCase))
            expanded = Path.ChangeExtension(expanded, wantedExtension);
        return expanded;
    }

    public static void Validate(ExportPreset preset)
    {
        _ = RequireCodec(preset.CodecId);
        if (preset.EncodeOptions is null) throw new InvalidDataException("Encode options are missing.");
        if (preset.EncodeOptions.Quality is < 1 or > 100)
            throw new InvalidDataException("Quality must be between 1 and 100.");
        if (preset.ResizeMode != ExportResizeMode.None && (preset.Width <= 0 || preset.Height <= 0))
            throw new InvalidDataException("Resize width and height must both be positive.");
        if (!ColorBgra.TryParseHexString(preset.PaddingColor, out _))
            throw new InvalidDataException("Padding color must be #RRGGBB, #AARRGGBB, or AARRGGBB.");
    }

    private static IImageCodec RequireCodec(string id)
    {
        var codec = CodecRegistry.FindById(id)
            ?? throw new CodecUnavailableException(id, "No codec is registered with this id.");
        if (!codec.CanEncode) throw new CodecUnavailableException(id, "This format is read-only.");
        if (!codec.IsAvailable) throw new CodecUnavailableException(id);
        return codec;
    }

    private static void ApplyResize(ref Document doc, ExportPreset preset)
    {
        if (preset.ResizeMode == ExportResizeMode.None) return;

        double sx = (double)preset.Width / doc.Width;
        double sy = (double)preset.Height / doc.Height;
        double scale = preset.ResizeMode == ExportResizeMode.FillAndCrop ? Math.Max(sx, sy) : Math.Min(sx, sy);
        if (!preset.AllowUpscale) scale = Math.Min(1, scale);

        switch (preset.ResizeMode)
        {
            case ExportResizeMode.Exact:
                Replace(ref doc, DocumentOps.Resize(doc, preset.Width, preset.Height));
                break;

            case ExportResizeMode.FitWithin:
            {
                int w = Math.Max(1, (int)Math.Round(doc.Width * scale));
                int h = Math.Max(1, (int)Math.Round(doc.Height * scale));
                if (w != doc.Width || h != doc.Height) Replace(ref doc, DocumentOps.Resize(doc, w, h));
                break;
            }

            case ExportResizeMode.FitAndPad:
            {
                int w = Math.Max(1, (int)Math.Round(doc.Width * scale));
                int h = Math.Max(1, (int)Math.Round(doc.Height * scale));
                Document scaled = w == doc.Width && h == doc.Height ? doc.Clone() : DocumentOps.Resize(doc, w, h);
                try
                {
                    var padded = new Document(preset.Width, preset.Height) { Dpi = doc.Dpi };
                    var background = padded.AddLayer("Export background");
                    ColorBgra.TryParseHexString(preset.PaddingColor, out var color);
                    background.Surface.Clear(color);
                    using Surface flat = scaled.Flatten();
                    var content = padded.AddLayer("Export content");
                    SurfaceOps.ShiftInto(content.Surface, flat,
                        (preset.Width - w) / 2, (preset.Height - h) / 2);
                    Replace(ref doc, padded);
                }
                finally { scaled.Dispose(); }
                break;
            }

            case ExportResizeMode.FillAndCrop:
            {
                int w = Math.Max(1, (int)Math.Ceiling(doc.Width * scale));
                int h = Math.Max(1, (int)Math.Ceiling(doc.Height * scale));
                using Document scaled = DocumentOps.Resize(doc, w, h);
                if (w >= preset.Width && h >= preset.Height)
                {
                    Replace(ref doc, DocumentOps.Crop(scaled,
                        (w - preset.Width) / 2, (h - preset.Height) / 2,
                        preset.Width, preset.Height));
                }
                else
                {
                    // Honouring AllowUpscale=false can leave one or both axes short. Keep the
                    // pixels at their real size and pad the uncovered part instead of quietly
                    // enlarging them just to satisfy the "fill" shape.
                    var padded = new Document(preset.Width, preset.Height) { Dpi = doc.Dpi };
                    var background = padded.AddLayer("Export background");
                    ColorBgra.TryParseHexString(preset.PaddingColor, out var color);
                    background.Surface.Clear(color);
                    using Surface flat = scaled.Flatten();
                    var content = padded.AddLayer("Export content");
                    SurfaceOps.ShiftInto(content.Surface, flat,
                        (preset.Width - w) / 2, (preset.Height - h) / 2);
                    Replace(ref doc, padded);
                }
                break;
            }
        }
    }

    private static void Replace(ref Document current, Document replacement)
    {
        current.Dispose();
        current = replacement;
    }

    private static void SaveAtomic(Document doc, string outputPath, ExportPreset preset)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        string temp = Path.Combine(dir, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = File.Create(temp))
            using (var flat = doc.Flatten())
            {
                var codec = RequireCodec(preset.CodecId);
                codec.Encode(flat, output, preset.EncodeOptions);
            }
            File.Move(temp, outputPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    private static Document LoadDocument(string inputPath)
    {
        if (inputPath.EndsWith(DocumentFile.Extension, StringComparison.OrdinalIgnoreCase))
            return DocumentFile.Load(inputPath);

        using var input = File.OpenRead(inputPath);
        using Surface surface = CodecRegistry.Decode(input, inputPath);
        var doc = new Document(surface.Width, surface.Height);
        var layer = doc.AddLayer(Path.GetFileNameWithoutExtension(inputPath));
        layer.Surface.CopyFrom(surface);
        return doc;
    }

    private static void WriteTextAtomic(string path, string contents)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    private static string ExpandText(string text, string sourceName, string presetName, int width, int height)
        => text
            .Replace("{name}", Path.GetFileNameWithoutExtension(sourceName), StringComparison.OrdinalIgnoreCase)
            .Replace("{preset}", presetName, StringComparison.OrdinalIgnoreCase)
            .Replace("{width}", width.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{height}", height.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
}
