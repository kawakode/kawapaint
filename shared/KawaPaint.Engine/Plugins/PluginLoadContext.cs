// KawaPaint — one isolated, collectible AssemblyLoadContext per plugin, so a plugin's own
// dependency DLLs (resolved via AssemblyDependencyResolver, same mechanism .NET's own plugin
// tutorial uses) never collide with another plugin's or the host's.
//
// Never touched on the browser build in practice: AssemblyLoadContext compiles fine into
// net10.0-browser (nothing here is desktop-specific API), but AppPluginHost only ever constructs
// one when it has a real plugin folder to load, and AppPaths.PluginsDirectory is null there — see
// AppPluginHost.LoadAll.

using System;
using System.Reflection;
using System.Runtime.Loader;

namespace KawaPaint.Engine.Plugins;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginId, string mainDllPath) : base(name: pluginId, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainDllPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Contract types (IKawaPaintPlugin, IEffect, ColorBgra, ...) must be the SAME loaded
        // assembly the host is using, or an `is IEffect`/cast check on the plugin's own objects
        // fails at runtime — ALC type identity is per-context. Always defer KawaPaint.Engine to
        // the default context, regardless of whether the plugin's own output folder happens to
        // contain a copy of it (the sample plugin's Private="false" reference avoids that, but a
        // real third-party plugin author might get it wrong).
        if (assemblyName.Name == "KawaPaint.Engine") return null;

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
