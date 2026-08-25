using System.Text;
using KawaPaint.App.Core.Scripting;
using KawaPaint.Engine;
using KawaPaint.Engine.Scripting;

namespace KawaPaint.Sandbox;

internal static class ScriptingV2SmokeTest
{
    public static void RunAll()
    {
        V2StringsRoundTripAndExecute();
        CropAndCurvesExecute();
        SeededEffectsReplayExactly();
        V1StillLoads();
        Console.WriteLine("SCRIPTING V2 SMOKE OK - strings, crop, Curves, deterministic seeds, v1 compatibility");
    }

    private static void CropAndCurvesExecute()
    {
        Assert(ScriptRecorder.IsScriptable("image.crop"), "crop is missing from the recorder allow-list");
        Assert(ScriptRecorder.IsScriptable("effect.curves"), "Curves is missing from the recorder allow-list");

        byte[] inverse = Enumerable.Range(0, 256).Select(i => (byte)(255 - i)).ToArray();
        var script = new ScriptFile { Title = "crop-curves" };
        script.Steps.Add(new ScriptStep("effect.curves", inverse.Select(x => (double)x).ToArray()));
        script.Steps.Add(new ScriptStep("image.crop", new double[] { 2, 1, 5, 4 }));

        var document = new Document(10, 8);
        try
        {
            Layer layer = document.AddLayer("pixels");
            for (int y = 0; y < document.Height; y++)
            for (int x = 0; x < document.Width; x++)
                layer.Surface[x, y] = ColorBgra.FromBgra((byte)(x * 9), (byte)(y * 13),
                    (byte)(x * 7 + y * 5), (byte)(120 + x + y));
            ColorBgra original = layer.Surface[2, 1];

            IReadOnlyList<ScriptStepResult> results = ScriptExecutor.Run(ref document, script);
            Assert(results.All(result => result.Outcome == ScriptStepOutcome.Applied),
                "crop/Curves script step did not apply");
            Assert(document.Width == 5 && document.Height == 4, "script crop dimensions are wrong");
            ColorBgra actual = document.Layers[0].Surface[0, 0];
            Assert(actual == ColorBgra.FromBgra((byte)(255 - original.B), (byte)(255 - original.G),
                    (byte)(255 - original.R), original.A),
                "Curves LUT or crop origin was not replayed exactly");
        }
        finally
        {
            document.Dispose();
        }
    }

    private static void SeededEffectsReplayExactly()
    {
        (string tag, double[] args)[] cases =
        [
            ("noise", [18, 123456]),
            ("frostedglass", [0, 4, 3, 234567]),
            ("dents", [25, 50, 10, 10, 345678])
        ];

        foreach ((string tag, double[] args) in cases)
        {
            IEffect first = ScriptEffects.Build(tag, args)
                ?? throw new InvalidOperationException($"could not build {tag}");
            IEffect second = ScriptEffects.Build(tag, args)
                ?? throw new InvalidOperationException($"could not rebuild {tag}");
            using Surface a = SeedPattern();
            using Surface b = SeedPattern();
            first.Apply(a);
            second.Apply(b);
            Assert(SameSurface(a, b), $"{tag} did not replay deterministically with its recorded seed");
        }
    }

    private static Surface SeedPattern()
    {
        var surface = new Surface(19, 17);
        for (int y = 0; y < surface.Height; y++)
        for (int x = 0; x < surface.Width; x++)
            surface[x, y] = ColorBgra.FromBgra((byte)(x * 11), (byte)(y * 13),
                (byte)(x * 3 + y * 7), 255);
        return surface;
    }

    private static bool SameSurface(Surface a, Surface b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
        for (int x = 0; x < a.Width; x++)
            if (a[x, y] != b[x, y]) return false;
        return true;
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
