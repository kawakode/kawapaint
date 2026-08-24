using System.Text.Json;
using KawaPaint.App.Core;
using KawaPaint.Engine.Exporting;

namespace KawaPaint.Sandbox;

internal static class CliPresetSmokeTest
{
    public static void RunAll()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kawa_smoke", "cli-presets");
        Directory.CreateDirectory(dir);
        string input = Path.Combine(Path.GetTempPath(), "kawa_smoke", "smoke.png");
        string settingsPath = Path.Combine(dir, "settings.json");

        var settings = new AppSettings
        {
            ExportPresets = new Dictionary<string, ExportPreset>(StringComparer.OrdinalIgnoreCase)
            {
                ["CLI Tiny"] = new()
                {
                    CodecId = "png", ResizeMode = ExportResizeMode.Exact,
                    Width = 9, Height = 7, FilenamePattern = "{name}-cli.{ext}"
                }
            }
        };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));
        int exit = KawaPaint.Cli.BatchCliRunner.Run(new[]
        {
            "--preset", "CLI Tiny", "--settings", settingsPath,
            "--in", input, "--out-dir", dir
        });
        if (exit != 0 || !File.Exists(Path.Combine(dir, "smoke-cli.png")))
            throw new InvalidOperationException("CLI preset smoke test failed");
        Console.WriteLine("CLI PRESET SMOKE OK");
    }
}
