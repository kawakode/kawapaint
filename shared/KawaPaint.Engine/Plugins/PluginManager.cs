// KawaPaint - discovers and loads plugins from a set of directories. Pure: takes plain data (no
// AppSettings/AppPaths dependency), so this exact method is callable from KawaPaint.App and
// headlessly from KawaPaint.Sandbox alike. A load failure anywhere (bad DLL, wrong/missing
// IKawaPaintPlugin, a throwing constructor or Register call, a folder/Id mismatch) is always
// caught and turned into a Failed PluginLoadResult - never allowed to propagate and take down
// startup, mirroring JxlCodec.IsAvailable's probe-and-degrade pattern.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KawaPaint.Engine.Plugins;

public static class PluginManager
{
    /// <summary>
    /// Each immediate subdirectory of each root is one plugin candidate, expected to contain
    /// exactly one file named "&lt;folder name&gt;.dll". A folder whose name is in
    /// <paramref name="disabledIds"/> is skipped before anything is loaded - disabled plugin code
    /// never executes.
    /// </summary>
    public static IReadOnlyList<PluginLoadResult> LoadFrom(IEnumerable<string> searchRoots, IReadOnlySet<string> disabledIds)
    {
        var results = new List<PluginLoadResult>();

        foreach (string root in searchRoots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string folderName = Path.GetFileName(dir);

                if (disabledIds.Contains(folderName))
                {
                    results.Add(new PluginLoadResult(folderName, null, null, PluginStatus.Disabled, null, dir));
                    continue;
                }

                results.Add(LoadOne(folderName, dir));
            }
        }

        return results;
    }

    private static PluginLoadResult LoadOne(string folderName, string dir)
    {
        string dllPath = Path.Combine(dir, folderName + ".dll");
        if (!File.Exists(dllPath))
            return new PluginLoadResult(folderName, null, null, PluginStatus.Failed,
                $"No {folderName}.dll found in plugin folder.", dir);

        try
        {
            var alc = new PluginLoadContext(folderName, dllPath);
            var asm = alc.LoadFromAssemblyPath(dllPath);

            var pluginTypes = asm.GetTypes()
                .Where(t => typeof(IKawaPaintPlugin).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
                .ToList();

            if (pluginTypes.Count == 0)
                return new PluginLoadResult(folderName, null, null, PluginStatus.Failed,
                    "No IKawaPaintPlugin implementation found in the assembly.", dir);
            if (pluginTypes.Count > 1)
                return new PluginLoadResult(folderName, null, null, PluginStatus.Failed,
                    $"Multiple IKawaPaintPlugin implementations found ({pluginTypes.Count}); exactly one is expected.", dir);

            var plugin = (IKawaPaintPlugin)Activator.CreateInstance(pluginTypes[0])!;

            if (!string.Equals(plugin.Id, folderName, StringComparison.OrdinalIgnoreCase))
                return new PluginLoadResult(folderName, plugin.DisplayName, plugin.Version, PluginStatus.Failed,
                    $"Declared Id '{plugin.Id}' does not match folder name '{folderName}'.", dir);

            var context = new PluginContext();
            plugin.Register(context);   // any throw here is caught below; nothing gets registered

            foreach (var effect in context.Effects) EffectRegistry.Register(effect);
            foreach (var tool in context.Tools) ToolRegistry.Register(tool);

            return new PluginLoadResult(plugin.Id, plugin.DisplayName, plugin.Version, PluginStatus.Loaded, null, dir);
        }
        catch (Exception ex)
        {
            return new PluginLoadResult(folderName, null, null, PluginStatus.Failed, ex.Message, dir);
        }
    }
}
