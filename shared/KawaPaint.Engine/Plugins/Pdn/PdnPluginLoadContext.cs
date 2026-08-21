// KawaPaint - one isolated, collectible AssemblyLoadContext per real third-party Paint.NET plugin
// DLL. The load-bearing piece here is the "PaintDotNet.*" redirect below: it makes plugin DLLs
// compiled 10+ years ago against an old paint.net version resolve against whatever real version is
// installed today, the classic-tier equivalent of a binding redirect (plain strong-name resolution
// in a custom ALC does not do this on its own - a version mismatch would otherwise fail outright).
// Mirrors KawaPaint's own PluginLoadContext (used for native plugins), one layer deeper: that one
// isolates a plugin's dependencies from other plugins; this one additionally has to reconcile a
// plugin DLL's dependency version against a real install it wasn't necessarily built against.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace KawaPaint.Engine.Plugins.Pdn;

internal sealed class PdnPluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly PdnAssembliesLoadContext _pdnAssemblies;

    public PdnPluginLoadContext(string pluginDllPath, PdnAssembliesLoadContext pdnAssemblies)
        : base(name: Path.GetFileNameWithoutExtension(pluginDllPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginDllPath);
        _pdnAssemblies = pdnAssemblies;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // A plugin's own private dependencies (if it ships any alongside its DLL) take priority
        // over paint.net's own copies of the same name.
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is not null) return LoadFromAssemblyPath(path);

        // Anything else - "PaintDotNet.*" at whatever version this plugin was originally compiled
        // against, plus paint.net's own dependency graph (System.Windows.Forms,
        // System.Drawing.Common, ComputeSharp, ...) that a single-DLL plugin doesn't ship itself -
        // resolves through the shared PdnAssembliesLoadContext instead.
        return assemblyName.Name is { } n ? _pdnAssemblies.TryResolve(n) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
