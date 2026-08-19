// KawaPaint — locates a real, separately-installed paint.net on this machine so the classic-tier
// plugin bridge (PdnEffectDiscovery) can load its assemblies at runtime.
//
// KawaPaint never bundles or ships any PaintDotNet.*.dll itself, and never references one at
// compile time — this fork exists specifically to get away from the restrictive license paint.net
// adopted after 3.36 (see FORK.TXT), and that license permits using/copying/distributing the
// unmodified software but forbids "modify, adapt, ... sell, or create derivative works". Bundling
// its binaries would sit far closer to that line than intended. Instead, this only ever points at
// an install the user already has separately on their own machine; see PdnAssembliesLoadContext
// for how that install's assemblies get loaded via pure reflection.
//
// TryValidate only reads the assembly manifest (AssemblyName.GetAssemblyName) — it never loads or
// executes anything — so it is safe to call speculatively against arbitrary user-picked folders.

using System;
using System.IO;
using System.Reflection;

namespace KawaPaint.Engine.Plugins.Pdn;

public sealed record PdnInstallInfo(string InstallDirectory, string EffectsCoreDllPath, Version Version);

public static class PdnInstallLocator
{
    private const string EffectsCoreDll = "PaintDotNet.Effects.Core.dll";

    /// <summary>Null when nothing valid is found (including on any platform without a plausible
    /// paint.net install at all — non-Windows, browser). A non-empty <paramref name="userOverride"/>
    /// always wins if it validates, even on non-Windows, so a user-supplied path is never silently
    /// second-guessed by the well-known-location probe.</summary>
    public static PdnInstallInfo? Locate(string? userOverride)
    {
        if (!string.IsNullOrWhiteSpace(userOverride))
            return TryValidate(userOverride);

        if (!OperatingSystem.IsWindows()) return null;

        foreach (string? candidate in WellKnownCandidates())
        {
            if (candidate is null) continue;
            if (TryValidate(candidate) is { } info) return info;
        }

        return null;
    }

    /// <summary>Valid iff PaintDotNet.Effects.Core.dll exists in this directory.</summary>
    public static PdnInstallInfo? TryValidate(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        try
        {
            string dllPath = Path.Combine(directory, EffectsCoreDll);
            if (!File.Exists(dllPath)) return null;

            Version version = AssemblyName.GetAssemblyName(dllPath).Version ?? new Version(0, 0);
            return new PdnInstallInfo(directory, dllPath, version);
        }
        catch
        {
            // A folder that exists but whose DLL is unreadable/corrupt/not a .NET assembly is
            // just "not a valid install" — never a reason to fail startup.
            return null;
        }
    }

    private static string?[] WellKnownCandidates() => new[]
    {
        CombineEnv("ProgramFiles", "paint.net"),
        CombineEnv("ProgramFiles(x86)", "paint.net"),
        CombineEnv("LocalAppData", "paint.net"),   // Microsoft Store per-user layout
    };

    private static string? CombineEnv(string variable, string subPath)
    {
        string? root = Environment.GetEnvironmentVariable(variable);
        return root is null ? null : Path.Combine(root, subPath);
    }
}
