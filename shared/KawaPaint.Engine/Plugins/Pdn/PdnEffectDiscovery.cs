// KawaPaint - entry point for the classic-tier Paint.NET plugin bridge. Mirrors PluginManager's
// shape but scans a flat folder of loose DLLs (one *.dll = one candidate) instead of one folder
// per plugin, matching how unmodified third-party plugin DLLs are actually distributed. Every
// failure (bad DLL, no PropertyBasedEffect types, no parameterless ctor, a paint.net API-shape
// mismatch) is caught per-DLL into a PluginLoadResult - never propagates and takes down discovery
// of the other DLLs in the folder, mirroring PluginManager's own never-crash-startup contract.
//
// Only PropertyBasedEffect-derived types are discovered, not plain Effect subclasses. A plain
// Effect (pre-IndirectUI, hand-rolled WinForms EffectConfigDialog, no PropertyCollection at all)
// has nothing this bridge can drive without showing paint.net's own dialog - which this phase
// deliberately never does (see PdnPropertyMapper's file header). PropertyBasedEffect has been the
// recommended, overwhelmingly common pattern for classic-tier plugins for well over a decade, so
// this is a narrow, honest scope boundary rather than a real coverage gap.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KawaPaint.Engine.Plugins.Pdn;

public static class PdnEffectDiscovery
{
    private const string Category = "Paint.NET Plugins";

    /// <summary>
    /// The shared paint.net assembly context and its resolved schema, cached per install directory
    /// and reused for the process lifetime.
    ///
    /// Not an optimization - a correctness requirement. PdnAssembliesLoadContext is deliberately
    /// <c>isCollectible: false</c> (see its file header), which is only sound if it's constructed
    /// once. Building a fresh one per LoadFrom call stranded the entire PaintDotNet.*.dll set -
    /// tens of MB, unreclaimable - on every plugin reload, and the Plugin Manager reaches reload
    /// from three separate buttons (Reload Plugins, Browse…, Auto-detect). Keyed by directory so
    /// pointing at a genuinely different install still builds a new context, rather than silently
    /// serving types from the old one.
    /// </summary>
    private static readonly Dictionary<string, (PdnAssembliesLoadContext Assemblies, PdnReflectionSchema Schema)> _bridgeCache
        = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<PluginLoadResult> LoadFrom(
        PdnInstallInfo? pdnInstall, IEnumerable<string> searchRoots, IReadOnlySet<string> disabledKeys)
    {
        var results = new List<PluginLoadResult>();
        if (pdnInstall is null) return results;

        PdnAssembliesLoadContext pdnAssemblies;
        PdnReflectionSchema schema;
        try
        {
            if (_bridgeCache.TryGetValue(pdnInstall.InstallDirectory, out var cached))
            {
                (pdnAssemblies, schema) = cached;
            }
            else
            {
                pdnAssemblies = new PdnAssembliesLoadContext(pdnInstall.InstallDirectory);
                schema = new PdnReflectionSchema(pdnAssemblies.LoadAll());
                // Cached only on full success: a half-built bridge (assemblies loaded, schema
                // lookup failed) must not be served to the next reload as if it were usable.
                _bridgeCache[pdnInstall.InstallDirectory] = (pdnAssemblies, schema);
            }
        }
        catch (Exception ex)
        {
            results.Add(new PluginLoadResult("pdn-bridge", null, pdnInstall.Version.ToString(), PluginStatus.Failed,
                ex.Message, pdnInstall.InstallDirectory));
            return results;
        }

        foreach (string root in searchRoots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (string dll in Directory.EnumerateFiles(root, "*.dll"))
                LoadDll(dll, pdnAssemblies, schema, disabledKeys, results);
        }

        return results;
    }

    private static void LoadDll(string dllPath, PdnAssembliesLoadContext pdnAssemblies, PdnReflectionSchema schema,
        IReadOnlySet<string> disabledKeys, List<PluginLoadResult> results)
    {
        string dllFile = Path.GetFileName(dllPath);

        try
        {
            var alc = new PdnPluginLoadContext(dllPath, pdnAssemblies);
            var asm = alc.LoadFromAssemblyPath(dllPath);

            var effectTypes = asm.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic
                            && schema.PropertyBasedEffectType.IsAssignableFrom(t)
                            && t.GetConstructor(Type.EmptyTypes) is not null)
                .ToList();

            if (effectTypes.Count == 0)
            {
                results.Add(new PluginLoadResult(dllFile, null, null, PluginStatus.Failed,
                    "No PropertyBasedEffect-derived type with a public parameterless constructor was found.", dllPath));
                return;
            }

            foreach (var effectType in effectTypes)
            {
                string key = dllFile + "::" + effectType.FullName;

                if (disabledKeys.Contains(key))
                {
                    results.Add(new PluginLoadResult(key, effectType.Name, null, PluginStatus.Disabled, null, dllPath));
                    continue;
                }

                results.Add(LoadOneEffect(key, effectType, schema, dllPath));
            }
        }
        catch (Exception ex)
        {
            results.Add(new PluginLoadResult(dllFile, null, null, PluginStatus.Failed, ex.Message, dllPath));
        }
    }

    private static PluginLoadResult LoadOneEffect(string key, Type effectType, PdnReflectionSchema schema, string dllPath)
    {
        try
        {
            object probe = Activator.CreateInstance(effectType)!;
            schema.EnvironmentParameters.SetValue(probe, schema.DefaultEnvironmentParameters.GetValue(null));
            object propertyCollection = schema.CreatePropertyCollection.Invoke(probe, null)!;
            PdnPropertyMapper.MapResult mapResult = PdnPropertyMapper.Map(propertyCollection, schema);

            string displayName = Humanize(effectType.Name);
            var parameters = mapResult.Properties.Where(p => p.Spec is not null).Select(p => p.Spec!).ToList();
            var propertiesForClosure = mapResult.Properties;

            var descriptor = new PluginEffectDescriptor(key, displayName, parameters,
                values => BuildAdapter(displayName, effectType, schema, propertiesForClosure, values),
                Category);

            EffectRegistry.Register(descriptor);

            string? note = mapResult.UnmappedNotes.Count > 0 ? string.Join(" ", mapResult.UnmappedNotes) : null;
            return new PluginLoadResult(key, displayName, null, PluginStatus.Loaded, note, dllPath);
        }
        catch (Exception ex)
        {
            return new PluginLoadResult(key, effectType.Name, null, PluginStatus.Failed, Unwrap(ex), dllPath);
        }
    }

    private static string Unwrap(Exception ex)
    {
        while (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
        return ex.GetType().Name + ": " + ex.Message;
    }

    private static IEffect BuildAdapter(string name, Type effectType, PdnReflectionSchema schema,
        IReadOnlyList<PdnPropertyMapper.MappedProperty> properties, PluginParameterValues values)
    {
        var current = new Dictionary<string, object>();
        foreach (var p in properties)
            if (p.Spec is not null)
                current[p.Spec.Key] = ReadValue(p.Spec, values);

        return new PdnClassicEffectAdapter(name, effectType, schema, properties, current);
    }

    private static object ReadValue(PluginParameterSpec spec, PluginParameterValues values) => spec switch
    {
        NumericParameterSpec => values.GetDouble(spec.Key),
        BoolParameterSpec => values.GetBool(spec.Key),
        ChoiceParameterSpec => values.GetChoiceIndex(spec.Key),
        ColorParameterSpec => values.GetColor(spec.Key),
        _ => throw new InvalidOperationException("Unknown parameter spec kind: " + spec.GetType())
    };

    private static string Humanize(string typeName)
    {
        string s = typeName.EndsWith("Effect", StringComparison.Ordinal) ? typeName[..^"Effect".Length] : typeName;
        if (s.Length == 0) return typeName;

        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
