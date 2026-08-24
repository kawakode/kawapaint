using KawaPaint.Engine.Exporting;

namespace KawaPaint.Engine.MailMerge;

public sealed record MailMergeRowResult(int RowNumber, string? OutputPath, string? Error);

public static class MailMergeRunner
{
    public static IReadOnlyList<MailMergeRowResult> Run(Document template, CsvData data,
        string sourceName, string presetName, ExportPreset preset, string outputFolder,
        string filenamePattern, string? scriptBaseDirectory = null)
    {
        if (template.DynamicTextZones.Count == 0)
            throw new InvalidOperationException("The template has no dynamic zones.");
        if (data.Rows.Count == 0) throw new InvalidOperationException("The CSV has no data rows.");

        var knownHeaders = new HashSet<string>(data.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var zone in template.DynamicTextZones)
            foreach (string field in Placeholders(zone.Template))
                if (!field.Equals("row", StringComparison.OrdinalIgnoreCase) && !knownHeaders.Contains(field))
                    throw new InvalidDataException($"Zone '{zone.Name}' refers to CSV field '{field}', but that header does not exist.");

        var results = new List<MailMergeRowResult>(data.Rows.Count);
        var usedPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < data.Rows.Count; i++)
        {
            try
            {
                using Document rendered = RenderRow(template, data.Rows[i], i + 1);
                ExportPreset rowPreset = ClonePreset(preset);
                rowPreset.FilenamePattern = Expand(filenamePattern, data.Rows[i], i + 1);
                if (!usedPatterns.Add(rowPreset.FilenamePattern))
                    throw new IOException("This row resolves to the same output filename as an earlier row. Add {row} or a unique CSV field to the filename pattern.");
                if (rowPreset.PackageText is not null)
                    rowPreset.PackageText = Expand(rowPreset.PackageText, data.Rows[i], i + 1);
                var result = PresetExporter.ExportFile(rendered, sourceName, presetName, rowPreset,
                    outputFolder, scriptBaseDirectory);
                results.Add(new MailMergeRowResult(i + 1, result.OutputPath, null));
            }
            catch (Exception ex) { results.Add(new MailMergeRowResult(i + 1, null, ex.Message)); }
        }
        return results;
    }

    public static Document RenderRow(Document template, IReadOnlyDictionary<string, string> values, int rowNumber = 0)
    {
        Document rendered = template.Clone();
        var layer = rendered.AddLayer("Mail merge values");
        foreach (var zone in rendered.DynamicTextZones)
        {
            string text = Expand(zone.Template, values, rowNumber);
            if (!ColorBgra.TryParseHexString(zone.Color, out var color)) color = ColorBgra.Black;
            TextOps.DrawTextBox(layer.Surface, text, zone.X, zone.Y, zone.Width, zone.Height,
                zone.FontSize, color, zone.FontFamily, zone.Wrap, zone.ShrinkToFit,
                zone.Alignment, zone.VerticalAlignment);
        }
        rendered.DynamicTextZones.Clear();
        return rendered;
    }

    public static string Expand(string template, IReadOnlyDictionary<string, string> values, int rowNumber)
    {
        string result = template.Replace("{row}", rowNumber.ToString(), StringComparison.OrdinalIgnoreCase);
        foreach (var pair in values)
            result = result.Replace("{" + pair.Key + "}", pair.Value, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private static IEnumerable<string> Placeholders(string template)
    {
        int start = 0;
        while ((start = template.IndexOf('{', start)) >= 0)
        {
            int end = template.IndexOf('}', start + 1);
            if (end < 0) yield break;
            if (end > start + 1) yield return template[(start + 1)..end];
            start = end + 1;
        }
    }

    private static ExportPreset ClonePreset(ExportPreset p) => new()
    {
        CodecId = p.CodecId,
        EncodeOptions = new() { Quality = p.EncodeOptions.Quality, Lossless = p.EncodeOptions.Lossless,
            IconSizes = p.EncodeOptions.IconSizes.ToArray() },
        ResizeMode = p.ResizeMode, Width = p.Width, Height = p.Height, AllowUpscale = p.AllowUpscale,
        PaddingColor = p.PaddingColor, Flatten = p.Flatten, FilenamePattern = p.FilenamePattern,
        OutputFolder = p.OutputFolder, ScriptPath = p.ScriptPath, PackageText = p.PackageText,
        CopyPackageTextToClipboard = p.CopyPackageTextToClipboard
    };
}
