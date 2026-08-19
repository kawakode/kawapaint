// KawaPaint — turns AppSettings.Plugins + AppPaths into the search roots PluginManager.LoadFrom
// needs, and remembers the last results for PluginManagerDialog to display.

using System;
using System.Collections.Generic;
using KawaPaint.Engine.Plugins;

namespace KawaPaint.App.Core.Plugins;

public static class AppPluginHost
{
    public static IReadOnlyList<PluginLoadResult> LastResults { get; private set; } = Array.Empty<PluginLoadResult>();

    public static void LoadAll(AppSettings settings)
    {
        if (!settings.Plugins.Enabled)
        {
            LastResults = Array.Empty<PluginLoadResult>();
            return;
        }

        var roots = new List<string>();
        if (AppPaths.PluginsDirectory is { } builtIn) roots.Add(builtIn);   // null on the browser build
        roots.AddRange(settings.Plugins.SearchPaths);

        var disabled = new HashSet<string>(settings.Plugins.Disabled, StringComparer.OrdinalIgnoreCase);
        LastResults = PluginManager.LoadFrom(roots, disabled);
    }

    /// <summary>Full clean re-discovery. Correct for newly-enabled/added plugins; a disabled
    /// plugin whose objects are still reachable (e.g. it's the active tool) won't have its
    /// collectible AssemblyLoadContext actually collected until those references drop — a real
    /// CLR constraint on collectible ALCs, not something this can paper over.</summary>
    public static void Reload(AppSettings settings)
    {
        EffectRegistry.Clear();
        ToolRegistry.Clear();
        LoadAll(settings);
    }
}
