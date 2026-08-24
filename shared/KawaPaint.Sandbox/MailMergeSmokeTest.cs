using KawaPaint.Engine;
using KawaPaint.Engine.Codecs;
using KawaPaint.Engine.Exporting;
using KawaPaint.Engine.MailMerge;

namespace KawaPaint.Sandbox;

internal static class MailMergeSmokeTest
{
    public static void RunAll()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kawa_smoke", "mail-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var template = new Document(320, 180);
        template.AddLayer("cover").Surface.Clear(ColorBgra.White);
        template.DynamicTextZones.Add(new DynamicTextZone
        {
            Name = "Student", Template = "{Student}", X = 10, Y = 40, Width = 300, Height = 70,
            FontSize = 54, Alignment = DynamicTextAlignment.Center, ShrinkToFit = true
        });
        template.DynamicTextZones.Add(new DynamicTextZone
        {
            Name = "Class", Template = "Class {Class}", X = 20, Y = 120, Width = 280, Height = 35,
            FontSize = 24, Alignment = DynamicTextAlignment.Center
        });

        string project = Path.Combine(dir, "cover.kwp");
        DocumentFile.Save(template, project);
        using (var loaded = DocumentFile.Load(project))
        {
            Assert(loaded.DynamicTextZones.Count == 2, "zones did not survive .kwp round-trip");
            Assert(loaded.DynamicTextZones[0].Template == "{Student}", "zone template changed on save");
        }

        string csv = "Student,Class,Note\r\n" + string.Join("\r\n",
            Enumerable.Range(1, 24).Select(i => $"Student {i},5B,\"Binder, number {i}\""));
        CsvData data = CsvData.Parse(csv);
        Assert(data.Rows.Count == 24 && data.Rows[0]["Note"] == "Binder, number 1", "quoted CSV parsing failed");
        Assert(CsvData.Parse("Nom;Classe\nZoé;5B").Rows[0]["Nom"] == "Zoé", "semicolon CSV detection failed");
        var multiline = CsvData.Parse("Name,Note\n\"Ada\nLovelace\",\"said \"\"hello\"\"\"");
        Assert(multiline.Rows[0]["Name"] == "Ada\nLovelace" && multiline.Rows[0]["Note"] == "said \"hello\"",
            "multiline/escaped CSV parsing failed");

        using (var resized = DocumentOps.Resize(template, 640, 360))
            Assert(resized.DynamicTextZones[0].X == 20 && resized.DynamicTextZones[0].Width == 600,
                "resize did not transform zones");
        using (var rotated = DocumentOps.Rotate90(template, true))
            Assert(rotated.DynamicTextZones.Count == 2 && rotated.DynamicTextZones[0].Width == 70,
                "rotate did not transform zones");
        using (var flattened = DocumentOps.Flatten(template))
            Assert(flattened.DynamicTextZones.Count == 2, "flatten dropped zones");

        var preset = new ExportPreset { CodecId = "png", FilenamePattern = "unused.{ext}" };
        var results = MailMergeRunner.Run(template, data, "cover.kwp", "Class Covers", preset, dir,
            "{Student}-{row}.{ext}");
        Assert(results.Count == 24 && results.All(r => r.Error is null), "not all 24 rows rendered");
        Assert(results.Select(r => r.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 24,
            "rows did not produce unique files");

        using var firstStream = File.OpenRead(results[0].OutputPath!);
        using var first = CodecRegistry.Decode(firstStream, results[0].OutputPath);
        using var lastStream = File.OpenRead(results[^1].OutputPath!);
        using var last = CodecRegistry.Decode(lastStream, results[^1].OutputPath);
        Assert(first.Width == 320 && first.Height == 180, "output dimensions changed");
        Assert(PixelDifference(first, last) > 0, "different CSV rows rendered identical images");

        var duplicateResults = MailMergeRunner.Run(template,
            CsvData.Parse("Student,Class\nSame,5B\nSame,5B"), "cover.kwp", "Duplicates", preset, dir,
            "{Student}.{ext}");
        Assert(duplicateResults[0].Error is null && duplicateResults[1].Error is not null,
            "duplicate output names were not refused");

        bool rejected = false;
        template.DynamicTextZones[0].Template = "{MissingHeader}";
        try { MailMergeRunner.Run(template, data, "cover.kwp", "Bad", preset, dir, "{row}.{ext}"); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "missing CSV header was not rejected");

        Console.WriteLine("MAIL MERGE SMOKE OK - 24 CSV rows, persistent zones, distinct rendered images");
    }

    private static int PixelDifference(Surface a, Surface b)
    {
        int different = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a[x, y] != b[x, y]) different++;
        return different;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Mail merge smoke test: " + message);
    }
}
