// KawaPaint — every Type/MethodInfo/ConstructorInfo/PropertyInfo the classic-tier bridge needs,
// resolved exactly once against the real paint.net assemblies. A paint.net API-shape mismatch
// (wrong version, unexpected future rename) throws one clear "PDN bridge unavailable: ..."
// exception right here at construction, instead of a confusing null-ref buried inside a per-effect
// Apply() call. No compile-time reference to any PaintDotNet.*.dll exists anywhere in this file or
// project — every type below is looked up by full name at runtime.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;

namespace KawaPaint.Engine.Plugins.Pdn;

internal sealed class PdnReflectionSchema
{
    public Type EffectType { get; }
    public Type PropertyBasedEffectType { get; }
    public Type PropertyBasedEffectConfigTokenType { get; }
    public Type SurfaceType { get; }
    public Type RenderArgsType { get; }

    public MethodInfo CreatePropertyCollection { get; }
    public MethodInfo SetPropertyValue { get; }
    public MethodInfo SetRenderInfo { get; }
    public MethodInfo Render { get; }

    /// <summary>Effect.EnvironmentParameters (settable) and the static
    /// EffectEnvironmentParameters.DefaultParameters factory. Some real effects (Twist/Bulge-
    /// family warps, fractals, tile) read EnvironmentParameters — e.g. to size a default relative
    /// to canvas dimensions — from inside OnCreatePropertyCollection itself, and throw if it was
    /// never set. Found by hitting this for real: ~9 of paint.net's own 44 bundled legacy effects
    /// failed discovery with a bare constructed instance until this was wired in. Phase 1 always
    /// uses the generic DefaultParameters, both when probing for property definitions and before
    /// rendering — good enough to avoid the crash, not a claim of full environment fidelity (real
    /// Fg/Bg color, canvas DPI, and selection aren't threaded through IEffect.Apply(Surface) at
    /// all, and doing so is out of scope for this phase).</summary>
    public PropertyInfo EnvironmentParameters { get; }
    public PropertyInfo DefaultEnvironmentParameters { get; }

    public ConstructorInfo TokenConstructor { get; }
    public ConstructorInfo SurfaceConstructor { get; }
    public ConstructorInfo RenderArgsConstructor { get; }

    public PropertyInfo SurfaceScan0 { get; }
    public PropertyInfo SurfaceStride { get; }
    public PropertyInfo MemoryBlockPointer { get; }

    public PropertyInfo PropertyName { get; }
    public PropertyInfo PropertyValue { get; }
    public PropertyInfo PropertyDefaultValue { get; }
    public PropertyInfo PropertyValueType { get; }

    public PdnReflectionSchema(IReadOnlyList<Assembly> pdnAssemblies)
    {
        EffectType = Find(pdnAssemblies, "PaintDotNet.Effects.Effect");
        PropertyBasedEffectType = Find(pdnAssemblies, "PaintDotNet.Effects.PropertyBasedEffect");
        Type effectConfigTokenType = Find(pdnAssemblies, "PaintDotNet.Effects.EffectConfigToken");
        PropertyBasedEffectConfigTokenType = Find(pdnAssemblies, "PaintDotNet.Effects.PropertyBasedEffectConfigToken");
        Type propertyCollectionType = Find(pdnAssemblies, "PaintDotNet.PropertySystem.PropertyCollection");
        Type propertyType = Find(pdnAssemblies, "PaintDotNet.PropertySystem.Property");
        Type memoryBlockType = Find(pdnAssemblies, "PaintDotNet.MemoryBlock");
        SurfaceType = Find(pdnAssemblies, "PaintDotNet.Surface");
        RenderArgsType = Find(pdnAssemblies, "PaintDotNet.RenderArgs");
        Type environmentParametersType = Find(pdnAssemblies, "PaintDotNet.Effects.EffectEnvironmentParameters");

        CreatePropertyCollection = Method(PropertyBasedEffectType, "CreatePropertyCollection", Type.EmptyTypes);
        SetPropertyValue = Method(PropertyBasedEffectConfigTokenType, "SetPropertyValue", new[] { typeof(object), typeof(object) });
        SetRenderInfo = Method(EffectType, "SetRenderInfo", new[] { effectConfigTokenType, RenderArgsType, RenderArgsType });
        Render = Method(EffectType, "Render", new[] { effectConfigTokenType, RenderArgsType, RenderArgsType, typeof(Rectangle[]) });

        TokenConstructor = Ctor(PropertyBasedEffectConfigTokenType, new[] { propertyCollectionType });
        SurfaceConstructor = Ctor(SurfaceType, new[] { typeof(int), typeof(int) });
        RenderArgsConstructor = Ctor(RenderArgsType, new[] { SurfaceType });

        SurfaceScan0 = Prop(SurfaceType, "Scan0");
        SurfaceStride = Prop(SurfaceType, "Stride");
        MemoryBlockPointer = Prop(memoryBlockType, "Pointer");

        PropertyName = Prop(propertyType, "Name");
        PropertyValue = Prop(propertyType, "Value");
        PropertyDefaultValue = Prop(propertyType, "DefaultValue");
        PropertyValueType = Prop(propertyType, "ValueType");

        EnvironmentParameters = Prop(EffectType, "EnvironmentParameters");
        DefaultEnvironmentParameters = Prop(environmentParametersType, "DefaultParameters");
    }

    private static Type Find(IReadOnlyList<Assembly> asms, string fullName)
    {
        foreach (Assembly a in asms)
        {
            Type? t = a.GetType(fullName, throwOnError: false);
            if (t is not null) return t;
        }
        throw new InvalidOperationException($"PDN bridge unavailable: type '{fullName}' was not found in any loaded paint.net assembly.");
    }

    private static MethodInfo Method(Type type, string name, Type[] parameterTypes)
        => type.GetMethod(name, parameterTypes)
           ?? throw new InvalidOperationException(
               $"PDN bridge unavailable: method '{type.FullName}.{name}({string.Join(",", parameterTypes.Select(p => p.Name))})' not found.");

    private static ConstructorInfo Ctor(Type type, Type[] parameterTypes)
        => type.GetConstructor(parameterTypes)
           ?? throw new InvalidOperationException(
               $"PDN bridge unavailable: constructor '{type.FullName}({string.Join(",", parameterTypes.Select(p => p.Name))})' not found.");

    private static PropertyInfo Prop(Type type, string name)
        => type.GetProperty(name)
           ?? throw new InvalidOperationException($"PDN bridge unavailable: property '{type.FullName}.{name}' not found.");
}
