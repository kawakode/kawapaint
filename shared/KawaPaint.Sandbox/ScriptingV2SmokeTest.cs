using System.Text;
using KawaPaint.Engine;
using KawaPaint.Engine.Scripting;

namespace KawaPaint.Sandbox;

internal static class ScriptingV2SmokeTest
{
    public static void RunAll()
    {
        V2StringsRoundTripAndExecute();
        V1StillLoads();
        Console.WriteLine("SCRIPTING V2 SMOKE OK - strings, text, rename, Clouds, v1 compatibility");
    }

    private static void V2StringsRoundTripAndExecute()
    {
        var script = new ScriptFile { Title = "strings" };
        script.Steps.Add(new ScriptStep("layer.rename", stringArgs: new[] { "Lettering" }));
        script.Steps.Add(new ScriptStep("text.draw", new double[] { 1, 1, 13, ColorBgra.Black.Bgra },
            new[] { "Hello" }));
        script.Steps.Add(new ScriptStep("effect.clouds", new double[] { 20, 0.5 },
            new[] { ColorBgra.Black.ToHexString(), ColorBgra.White.ToHexString() }));

        using var encoded = new MemoryStream();
        script.Save(encoded);
        encoded.Position = 0;
        ScriptFile decoded = ScriptFile.Load(encoded);
        Assert(decoded.Steps[1].StringArgs.Single() == "Hello", "script string did not round-trip");

        var document = new Document(32, 24);
        try
        {
            document.AddLayer("Layer 1");
            var results = ScriptExecutor.Run(ref document, decoded);
            Assert(results.All(result => result.Outcome == ScriptStepOutcome.Applied), "v2 script step did not apply");
            Assert(document.Layers[0].Name == "Lettering", "script layer rename did not apply");
            Assert(HasNonTransparentPixel(document.Layers[0].Surface), "script text/effect did not change pixels");
        }
        finally
        {
            document.Dispose();
        }
    }

    private static void V1StillLoads()
    {
        const string v1 = """
            {"FormatVersion":1,"Title":"old","AppVersion":"1","RecordedUtc":"2026-01-01T00:00:00Z",
             "Steps":[{"Id":"image.flipH","Args":[]}]}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(v1));
        ScriptFile decoded = ScriptFile.Load(stream);
        Assert(decoded.Steps.Count == 1 && decoded.Steps[0].StringArgs.Count == 0,
            "v1 script compatibility failed");
    }

    private static unsafe bool HasNonTransparentPixel(Surface surface)
    {
        for (int y = 0; y < surface.Height; y++)
        {
            ColorBgra* row = (ColorBgra*)surface.GetRowPointer(y);
            for (int x = 0; x < surface.Width; x++)
                if (row[x].A != 0) return true;
        }
        return false;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
