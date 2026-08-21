// KawaPaint - the IEffect that actually drives a real Paint.NET classic-tier plugin's Render(...).
// Builds a fresh effect instance + PropertyBasedEffectConfigToken on every Apply() call, matching
// how PluginEffectDescriptor.Build(values) is already invoked fresh on every PluginEffectDialog
// preview tick - no shared mutable state across calls. One-shot, whole-surface, single ROI, no
// cancellation: deliberately matches KawaPaint's own IEffect contract exactly rather than exposing
// paint.net's tiling/multi-ROI/incremental rendering.

using System;
using System.Collections.Generic;
using System.Drawing;

namespace KawaPaint.Engine.Plugins.Pdn;

internal sealed class PdnClassicEffectAdapter : IEffect
{
    private readonly Type _effectType;
    private readonly PdnReflectionSchema _schema;
    private readonly IReadOnlyList<PdnPropertyMapper.MappedProperty> _properties;
    private readonly IReadOnlyDictionary<string, object> _currentValues;

    public PdnClassicEffectAdapter(string name, Type effectType, PdnReflectionSchema schema,
        IReadOnlyList<PdnPropertyMapper.MappedProperty> properties, IReadOnlyDictionary<string, object> currentValues)
    {
        Name = name;
        _effectType = effectType;
        _schema = schema;
        _properties = properties;
        _currentValues = currentValues;
    }

    public string Name { get; }

    public void Apply(Surface surface)
    {
        object effectInstance = Activator.CreateInstance(_effectType)!;
        _schema.EnvironmentParameters.SetValue(effectInstance, _schema.DefaultEnvironmentParameters.GetValue(null));
        object propertyCollection = _schema.CreatePropertyCollection.Invoke(effectInstance, null)!;
        object token = _schema.TokenConstructor.Invoke(new object[] { propertyCollection })!;

        foreach (var p in _properties)
        {
            object uiValue = p.Spec is not null && _currentValues.TryGetValue(p.Spec.Key, out object? v) ? v : p.DefaultValue;
            object realValue = p.Spec is null ? p.DefaultValue : Coerce(uiValue, p);
            _schema.SetPropertyValue.Invoke(token, new object[] { p.PdnName, realValue });
        }

        using Surface srcClone = surface.Clone();

        // Both targets are disposed on the way out (see PdnRenderTarget): each holds a full-canvas
        // native buffer plus a GDI+ Bitmap/Graphics pair, and Apply() runs once per debounced
        // preview tick during a slider drag - not once per committed effect.
        using var dst = PdnSurfaceBridge.Wrap(surface, _schema);
        using var src = PdnSurfaceBridge.Wrap(srcClone, _schema);

        var rois = new[] { new Rectangle(0, 0, surface.Width, surface.Height) };
        _schema.SetRenderInfo.Invoke(effectInstance, new[] { token, dst.RenderArgs, src.RenderArgs });
        _schema.Render.Invoke(effectInstance, new object[] { token, dst.RenderArgs, src.RenderArgs, rois });

        PdnSurfaceBridge.CopyBack(surface, dst.PdnSurface, _schema);
    }

    /// <summary>Converts a value read back from PluginParameterValues (double/bool/choice-index
    /// int/ColorBgra, depending on which PluginParameterSpec kind produced it) into whatever CLR
    /// type the real paint.net Property.Value setter expects for this specific property.</summary>
    private static object Coerce(object uiValue, PdnPropertyMapper.MappedProperty p)
    {
        if (p.IsColorHeuristic)
        {
            var c = (ColorBgra)uiValue;
            int packed = (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
            return Convert.ChangeType(packed, p.RealValueType);
        }

        if (p.ChoiceValues is not null)
        {
            int index = (int)uiValue;
            return p.ChoiceValues[Math.Clamp(index, 0, p.ChoiceValues.Length - 1)];
        }

        if (p.RealValueType == typeof(bool)) return Convert.ToBoolean(uiValue);

        return Convert.ChangeType(uiValue, p.RealValueType);
    }
}
