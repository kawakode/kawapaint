// KawaPaint — headless verification of the classic-tier Paint.NET plugin bridge against a real
// local paint.net install, no mocks. Mirrors PluginSmokeTest.cs's "exercise the real production
// code path" philosophy. Gated on an env var pointing at a real install directory so CI and other
// developers' machines (which don't have paint.net installed) skip gracefully rather than failing.
//
// Uses PaintDotNet.Effects.Legacy.dll's real GaussianBlurEffect as the stand-in "third-party"
// plugin DLL — it's a genuine InternalPropertyBasedEffect : PropertyBasedEffect, exercising the
// exact same public contract a real third-party classic-tier plugin uses, and its expected output
// (a correct Gaussian blur on a sharp red/blue edge) was already hand-verified against paint.net's
// real Render(...) in the original research spike for this feature — this test re-checks that same
// characteristic result through the actual production PdnEffectDiscovery/PdnClassicEffectAdapter
// path, not a reimplementation of it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KawaPaint.Engine;
using KawaPaint.Engine.Plugins;
using KawaPaint.Engine.Plugins.Pdn;

namespace KawaPaint.Sandbox;

public static class PdnPluginSmokeTest
{
    private const string EnvVar = "KAWAPAINT_PDN_TEST_INSTALL_DIR";

    public static void RunAll()
    {
        string? installDir = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(installDir))
        {
            Console.WriteLine($"PDN PLUGIN SMOKE SKIPPED — set {EnvVar} to a real paint.net install directory to run this.");
            return;
        }

        var install = PdnInstallLocator.TryValidate(installDir);
        if (install is null)
        {
            Console.WriteLine($"PDN PLUGIN SMOKE SKIPPED — {installDir} does not look like a valid paint.net install (no PaintDotNet.Effects.Core.dll found).");
            return;
        }

        string legacyDll = Path.Combine(install.InstallDirectory, "PaintDotNet.Effects.Legacy.dll");
        if (!File.Exists(legacyDll))
        {
            Console.WriteLine("PDN PLUGIN SMOKE SKIPPED — PaintDotNet.Effects.Legacy.dll not found in the install directory.");
            return;
        }

        string scratch = Path.Combine(Path.GetTempPath(), "kawa_pdn_plugin_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            File.Copy(legacyDll, Path.Combine(scratch, "PaintDotNet.Effects.Legacy.dll"));

            var results = PdnEffectDiscovery.LoadFrom(install, new[] { scratch }, new HashSet<string>());
            Assert(results.Count > 0, "expected at least one result from a DLL containing paint.net's own built-in legacy effects");

            int loaded = results.Count(r => r.Status == PluginStatus.Loaded);
            Assert(loaded >= 30, $"expected most of paint.net's ~44 bundled legacy effects to load standalone, got {loaded}/{results.Count}");

            var blurResult = results.FirstOrDefault(r => r.Id.EndsWith("GaussianBlurEffect", StringComparison.Ordinal));
            Assert(blurResult is not null, "GaussianBlurEffect was not discovered in PaintDotNet.Effects.Legacy.dll");
            Assert(blurResult!.Status == PluginStatus.Loaded, $"expected Loaded, got {blurResult.Status} ({blurResult.Error})");

            var descriptor = EffectRegistry.FindById(blurResult.Id);
            Assert(descriptor is not null, "GaussianBlurEffect was not registered in EffectRegistry");
            Assert(descriptor!.Category == "Paint.NET Plugins", "expected the fixed 'Paint.NET Plugins' category");

            var radiusParam = descriptor.Parameters.OfType<NumericParameterSpec>().FirstOrDefault(p => p.Key == "Radius");
            Assert(radiusParam is not null, "expected a 'Radius' NumericParameterSpec on GaussianBlurEffect");

            var values = new PluginParameterValues(new Dictionary<string, object> { ["Radius"] = 8.0 });
            IEffect effect = descriptor.Build(values);

            using var surface = new Surface(40, 40);
            for (int y = 0; y < 40; y++)
                for (int x = 0; x < 40; x++)
                    surface[x, y] = x < 20 ? ColorBgra.FromBgr(0, 0, 255) : ColorBgra.FromBgr(255, 0, 0);

            effect.Apply(surface);

            ColorBgra mid = surface[20, 20];
            ColorBgra farLeft = surface[2, 20];
            ColorBgra farRight = surface[37, 20];

            bool blended = mid.R > 10 && mid.B > 10;
            bool edgesPreserved = farLeft.R > 200 && farRight.B > 200;
            Assert(blended && edgesPreserved,
                $"expected a blended edge and preserved far pixels; got farLeft={farLeft} mid={mid} farRight={farRight}");
        }
        finally
        {
            // Best-effort: the collectible PdnPluginLoadContext's file lock on the copied DLL
            // isn't released deterministically — same real constraint PluginSmokeTest.cs already
            // documents for native plugins. A leftover temp directory is harmless.
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }

        Console.WriteLine("PDN PLUGIN SMOKE OK — real paint.net GaussianBlurEffect discovered, registered, driven via PropertyCollection, and rendered a correct blur outside paint.net.exe.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("PDN PLUGIN SMOKE FAIL: " + message);
    }
}
