// KawaPaint — headless plugin-loader verification against the real production PluginManager, no
// mocks. Program.cs is top-level statements (one file only), so this lives as a separate static
// class it calls into. Exercises the happy path plus every degenerate case the loader is supposed
// to survive without crashing: a bad DLL, a disabled plugin, a plugin whose Register() throws
// partway through (atomic-rollback check), and a folder/Id mismatch.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KawaPaint.Engine;
using KawaPaint.Engine.Plugins;

namespace KawaPaint.Sandbox;

public static class PluginSmokeTest
{
    public static void RunAll()
    {
        string sampleDll = PluginDll("KawaPaint.Plugins.Sample");
        string fixtureDll = PluginDll("KawaPaint.Plugins.ThrowingFixture");
        if (!File.Exists(sampleDll)) throw new InvalidOperationException("Sample plugin DLL not found: " + sampleDll);
        if (!File.Exists(fixtureDll)) throw new InvalidOperationException("Throwing fixture DLL not found: " + fixtureDll);

        string scratch = Path.Combine(Path.GetTempPath(), "kawa_plugin_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            HappyPath(scratch, sampleDll);
            BadDll(scratch);
            Disabled(scratch, sampleDll);
            ThrowingRegister(scratch, fixtureDll);
            IdMismatch(scratch, sampleDll);
        }
        finally
        {
            // Best-effort: on Windows, a loaded assembly's DLL file stays locked until its
            // (collectible) AssemblyLoadContext is actually unloaded and collected, which isn't
            // deterministic — the same real constraint documented for plugin disable/reload in
            // AppPluginHost.Reload. A leftover temp directory is harmless; failing the whole test
            // run over an OS file lock would not be.
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }

        Console.WriteLine("PLUGIN SMOKE OK — happy=Loaded+Apply-changed-pixels, badDll=Failed, " +
                           "disabled=Disabled+no-register, throwingRegister=Failed+rollback-verified, idMismatch=Failed");
    }

    private static void HappyPath(string scratch, string sampleDll)
    {
        string dir = Deploy(scratch, "happy", "GlowTint", sampleDll);

        var results = PluginManager.LoadFrom(new[] { Path.Combine(scratch, "happy") }, new HashSet<string>());
        var result = results.Single();
        Assert(result.Status == PluginStatus.Loaded, $"expected Loaded, got {result.Status} ({result.Error})");
        Assert(result.Id == "GlowTint", "expected plugin Id 'GlowTint'");

        var descriptor = EffectRegistry.FindById("GlowTint.tint");
        Assert(descriptor is not null, "GlowTint.tint was not registered in EffectRegistry");
        Assert(descriptor!.Parameters.Count == 3, "expected 3 parameters (amount/invert/tint)");

        var values = new PluginParameterValues(new Dictionary<string, object>
        {
            ["amount"] = 100.0,
            ["invert"] = false,
            ["tint"] = ColorBgra.FromBgr(0, 0, 255)
        });
        var effect = descriptor.Build(values);

        using var surface = new Surface(8, 8);
        surface.Clear(ColorBgra.White);
        effect.Apply(surface);

        bool changed = false;
        for (int y = 0; y < surface.Height && !changed; y++)
            for (int x = 0; x < surface.Width && !changed; x++)
                if (surface[x, y] != ColorBgra.White) changed = true;
        Assert(changed, "GlowTint effect did not change any pixels — parameter flow-through is broken");

        _ = dir;
    }

    private static void BadDll(string scratch)
    {
        string dir = Path.Combine(scratch, "bad", "BadOne");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a dll");

        var results = PluginManager.LoadFrom(new[] { Path.Combine(scratch, "bad") }, new HashSet<string>());
        var result = results.Single();
        Assert(result.Status == PluginStatus.Failed, "a folder with no matching DLL should fail to load");
        Assert(!string.IsNullOrEmpty(result.Error), "a failed load should report a non-empty error message");
        Assert(EffectRegistry.FindById("GlowTint.tint") is not null, "unrelated earlier registration should be untouched by an unrelated failure");
    }

    private static void Disabled(string scratch, string sampleDll)
    {
        EffectRegistry.Clear();
        Deploy(scratch, "disabled", "GlowTint", sampleDll);

        var results = PluginManager.LoadFrom(new[] { Path.Combine(scratch, "disabled") }, new HashSet<string> { "GlowTint" });
        var result = results.Single();
        Assert(result.Status == PluginStatus.Disabled, $"expected Disabled, got {result.Status}");
        Assert(EffectRegistry.All.Count == 0, "a disabled plugin must never register anything — its code must never run");
    }

    private static void ThrowingRegister(string scratch, string fixtureDll)
    {
        EffectRegistry.Clear();
        Deploy(scratch, "throw", "ThrowingFixture", fixtureDll);

        var results = PluginManager.LoadFrom(new[] { Path.Combine(scratch, "throw") }, new HashSet<string>());
        var result = results.Single();
        Assert(result.Status == PluginStatus.Failed, $"a throwing Register() should fail the load, got {result.Status}");
        Assert(EffectRegistry.FindById("ThrowingFixture.neverRegistered") is null,
            "rollback failed: an effect registered before the throw leaked into EffectRegistry");
    }

    private static void IdMismatch(string scratch, string sampleDll)
    {
        EffectRegistry.Clear();
        Deploy(scratch, "mismatch", "WrongName", sampleDll);   // real GlowTint.dll bytes, wrong folder name

        var results = PluginManager.LoadFrom(new[] { Path.Combine(scratch, "mismatch") }, new HashSet<string>());
        var result = results.Single();
        Assert(result.Status == PluginStatus.Failed, "a folder/Id mismatch should fail the load");
        Assert(result.Error is not null && result.Error.Contains("does not match folder name"),
            $"unexpected error message for a mismatch: {result.Error}");
    }

    private static string Deploy(string scratch, string scenario, string folderName, string sourceDll)
    {
        string dir = Path.Combine(scratch, scenario, folderName);
        Directory.CreateDirectory(dir);
        File.Copy(sourceDll, Path.Combine(dir, folderName + ".dll"));
        return dir;
    }

    /// <summary>Locates a plugin project's built DLL relative to this Sandbox build's own output
    /// directory, matching whatever configuration/TFM Sandbox itself was built with (Debug/Release,
    /// net10.0) rather than hardcoding either.</summary>
    private static string PluginDll(string projectName)
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        string tfm = baseDir.Name;
        string config = baseDir.Parent!.Name;

        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "KawaPaint.slnx")))
            repoRoot = repoRoot.Parent;
        if (repoRoot is null) throw new InvalidOperationException("Could not locate repo root (KawaPaint.slnx) above " + AppContext.BaseDirectory);

        return Path.Combine(repoRoot.FullName, "plugins", projectName, "bin", config, tfm, projectName + ".dll");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("PLUGIN SMOKE FAIL: " + message);
    }
}
