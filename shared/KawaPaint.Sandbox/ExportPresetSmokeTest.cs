using KawaPaint.Engine;
using KawaPaint.Engine.Codecs;
using KawaPaint.Engine.Exporting;
using KawaPaint.Engine.Scripting;

namespace KawaPaint.Sandbox;

internal static class ExportPresetSmokeTest
{
    public static void RunAll()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kawa_smoke", "export-presets");
        Directory.CreateDirectory(dir);

        using var doc = new Document(40, 20);
        var baseLayer = doc.AddLayer("base");
        baseLayer.Surface.Clear(ColorBgra.FromBgr(0, 0, 220));
        var overlay = doc.AddLayer("overlay");
        overlay.Surface.Clear(ColorBgra.FromBgra(200, 0, 0, 80));

        var square = new ExportPreset
        {
            CodecId = "png",
            ResizeMode = ExportResizeMode.FitAndPad,
            Width = 30,
            Height = 30,
            PaddingColor = "FFFFFFFF",
            FilenamePattern = "../{name}-{preset}.{ext}",
            OutputFolder = dir,
            PackageText = "{name} {width}x{height}"
        };
        var result = PresetExporter.ExportFile(doc, "source.kwp", "square", square);
        Assert(Path.GetDirectoryName(result.OutputPath) == dir, "filename pattern escaped output directory");
        Assert(File.Exists(result.OutputPath), "preset image was not written");
        Assert(result.SidecarPath is not null && File.ReadAllText(result.SidecarPath) == "source 30x30",
            "package sidecar mismatch");
        using (var file = File.OpenRead(result.OutputPath))
        using (var decoded = CodecRegistry.Decode(file, result.OutputPath))
        {
            Assert(decoded.Width == 30 && decoded.Height == 30, "fit-and-pad dimensions mismatch");
            Assert(decoded[0, 0] == ColorBgra.White, "fit-and-pad did not use the requested background");
            Assert(decoded[15, 15] != ColorBgra.White, "fit-and-pad lost centered content");
        }

        var fit = new ExportPreset
        {
            CodecId = "png", ResizeMode = ExportResizeMode.FitWithin,
            Width = 100, Height = 100, AllowUpscale = false
        };
        using (var prepared = PresetExporter.Prepare(doc, fit, null, out _))
            Assert(prepared.Width == 40 && prepared.Height == 20, "FitWithin enlarged despite AllowUpscale=false");

        var crop = new ExportPreset
        {
            CodecId = "png", ResizeMode = ExportResizeMode.FillAndCrop,
            Width = 12, Height = 12
        };
        using (var prepared = PresetExporter.Prepare(doc, crop, null, out _))
            Assert(prepared.Width == 12 && prepared.Height == 12, "FillAndCrop dimensions mismatch");

        string scriptPath = Path.Combine(dir, "invert.kpscript");
        var script = new ScriptFile();
        script.Steps.Add(new ScriptStep("effect.invert"));
        script.Save(scriptPath);
        var scripted = new ExportPreset { CodecId = "png", ScriptPath = scriptPath };
        using (var prepared = PresetExporter.Prepare(doc, scripted, null, out var steps))
        {
            Assert(steps.Count == 1 && steps[0].Outcome == ScriptStepOutcome.Applied,
                "preset script did not run");
            using var scriptedPixels = prepared.Flatten();
            using var originalPixels = doc.Flatten();
            Assert(scriptedPixels[0, 0] != originalPixels[0, 0], "preset script did not alter pixels");
        }

        Assert(Directory.GetFiles(dir, "*.tmp").Length == 0, "preset export left temporary files behind");
        Console.WriteLine("EXPORT PRESET SMOKE OK");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Export preset smoke test: " + message);
    }
}
