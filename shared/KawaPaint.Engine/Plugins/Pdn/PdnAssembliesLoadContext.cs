// KawaPaint — one shared, non-collectible AssemblyLoadContext holding the real PaintDotNet.*.dll
// set (and their own transitive dependencies — System.Drawing.Common, System.Windows.Forms,
// ComputeSharp, TerraFX, etc., all confirmed to ship as loose files inside a self-contained
// portable paint.net install) for the process lifetime. Not collectible, by design: once loaded,
// these live as long as the app does, the same way paint.net itself never unloads its own effect
// assemblies mid-session.
//
// Resolves ANY assembly name by probing "<installDirectory>/<name>.dll" directly. This is
// deliberately broad (not limited to "PaintDotNet.*") because PaintDotNet.Effects.Core.dll's own
// dependency closure reaches System.Drawing.Common/System.Windows.Forms/etc., and those must
// resolve from this same isolated context too — .NET's per-ALC assembly identity means a separate
// copy of these living here never collides with whatever KawaPaint's own default context has
// loaded under the same simple name.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace KawaPaint.Engine.Plugins.Pdn;

internal sealed class PdnAssembliesLoadContext : AssemblyLoadContext
{
    private readonly string _installDirectory;
    private readonly Dictionary<string, Assembly> _loadedByName = new(StringComparer.Ordinal);

    public PdnAssembliesLoadContext(string installDirectory) : base(name: "KawaPaint.PdnBridge", isCollectible: false)
    {
        _installDirectory = installDirectory;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
        => assemblyName.Name is { } n ? TryResolve(n) : null;

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string path = Path.Combine(_installDirectory, unmanagedDllName);
        if (!File.Exists(path)) path = Path.Combine(_installDirectory, unmanagedDllName + ".dll");
        return File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    /// <summary>Eagerly loads every PaintDotNet.*.dll in the install directory (skipping any that
    /// fail to load as a managed assembly — e.g. PaintDotNet.SystemLayer.Native.x64.dll — those
    /// aren't fatal, just not useful here). PdnReflectionSchema then searches across the whole
    /// loaded set rather than assuming in advance which specific DLL a given type lives in, since
    /// that has moved between paint.net releases (PropertyCollection itself, for example, is
    /// declared in PaintDotNet.Base.dll, not PaintDotNet.PropertySystem.dll, as of 5.1.12).</summary>
    public IReadOnlyList<Assembly> LoadAll()
    {
        var loaded = new List<Assembly>();
        foreach (string dll in Directory.EnumerateFiles(_installDirectory, "PaintDotNet.*.dll"))
        {
            try
            {
                Assembly asm = LoadFromAssemblyPath(dll);
                loaded.Add(asm);
                if (asm.GetName().Name is { } name) _loadedByName[name] = asm;
            }
            catch
            {
                // Not every PaintDotNet.*.dll is a plain loadable managed assembly.
            }
        }

        if (loaded.Count == 0)
            throw new InvalidOperationException(
                "PDN bridge unavailable: no PaintDotNet.*.dll assemblies could be loaded from " + _installDirectory);

        return loaded;
    }

    /// <summary>Resolves an assembly by simple name: reused by this context's own Load()
    /// override, and by PdnPluginLoadContext for anything a third-party plugin's own local
    /// resolver can't find (its "PaintDotNet.*" references at whatever version it was originally
    /// compiled against, but also e.g. System.Windows.Forms/System.Drawing.Common — part of
    /// paint.net's own dependency graph, not something a single-DLL plugin ships itself).
    ///
    /// Tries the default context FIRST, so BCL-adjacent types like System.Drawing.Rectangle keep
    /// the SAME type identity KawaPaint.Engine's own compiled code uses — loading a second copy
    /// here would silently break any exact-signature GetMethod/GetConstructor lookup involving
    /// such a type. Caught for real during development: an earlier version of this method always
    /// probed the install directory first, which shadowed System.Drawing.Primitives and made
    /// PdnReflectionSchema's Rectangle[]-typed lookup for Effect.Render fail even though the
    /// method genuinely exists.</summary>
    public Assembly? TryResolve(string name)
    {
        if (_loadedByName.TryGetValue(name, out Assembly? cached)) return cached;

        try
        {
            Assembly fromDefault = Default.LoadFromAssemblyName(new AssemblyName(name));
            _loadedByName[name] = fromDefault;
            return fromDefault;
        }
        catch
        {
            // Not available from the default context — fall through to probing paint.net's own
            // install directory (the real PaintDotNet.*.dll set, plus support libraries that only
            // paint.net ships: ComputeSharp, TerraFX, System.Windows.Forms, etc.).
        }

        string path = Path.Combine(_installDirectory, name + ".dll");
        if (!File.Exists(path)) return null;

        try
        {
            Assembly asm = LoadFromAssemblyPath(path);
            _loadedByName[name] = asm;
            return asm;
        }
        catch
        {
            return null;
        }
    }
}
