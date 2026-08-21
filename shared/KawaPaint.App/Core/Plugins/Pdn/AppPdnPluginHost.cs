// KawaPaint - turns AppSettings.PdnPlugins + AppPaths into what PdnEffectDiscovery needs: locates
// a real, separately-installed paint.net (never bundled by KawaPaint itself - see
// PdnInstallLocator/PdnAssembliesLoadContext for why) and scans a flat folder of loose plugin DLLs
// for real classic-tier Paint.NET plugins. Deliberately parallel to AppPluginHost.cs, not merged
// with it - the two tiers use different folder conventions (one folder per plugin vs. one loose
// DLL per plugin) and PdnEffectDiscovery needs a PdnInstallInfo that AppPluginHost has no concept of.

using System;
using System.Collections.Generic;
using KawaPaint.Engine.Plugins;
using KawaPaint.Engine.Plugins.Pdn;

namespace KawaPaint.App.Core.Plugins.Pdn;

public static class AppPdnPluginHost
{
    public static PdnInstallInfo? InstallInfo { get; private set; }
    public static IReadOnlyList<PluginLoadResult> LastResults { get; private set; } = Array.Empty<PluginLoadResult>();

    public static void LoadAll(AppSettings settings)
    {
        if (!settings.PdnPlugins.Enabled)
        {
            InstallInfo = null;
            LastResults = Array.Empty<PluginLoadResult>();
            return;
        }

        InstallInfo = PdnInstallLocator.Locate(settings.PdnPlugins.InstallDirectoryOverride);

        var roots = new List<string>();
        if (AppPaths.PdnPluginsDirectory is { } builtIn) roots.Add(builtIn);   // null on the browser build
        roots.AddRange(settings.PdnPlugins.SearchPaths);

        var disabled = new HashSet<string>(settings.PdnPlugins.Disabled, StringComparer.OrdinalIgnoreCase);
        LastResults = PdnEffectDiscovery.LoadFrom(InstallInfo, roots, disabled);
    }

    /// <summary>Reloads both plugin tiers together, since they share one EffectRegistry/
    /// ToolRegistry and Clear() has no "clear only mine" mode - same limitation
    /// AppPluginHost.Reload already accepts for native plugins today.</summary>
    public static void Reload(AppSettings settings)
    {
        EffectRegistry.Clear();
        ToolRegistry.Clear();
        AppPluginHost.LoadAll(settings);
        LoadAll(settings);
    }
}
