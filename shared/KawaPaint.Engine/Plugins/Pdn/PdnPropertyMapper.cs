// KawaPaint — maps a real PaintDotNet.PropertySystem.PropertyCollection onto KawaPaint's own
// (much narrower) PluginParameterSpec set, reusing the existing PluginEffectDialog UI unchanged.
//
// Explicit, honest fallbacks (documented limitations, not solved further here):
//   - A property type this mapper doesn't recognise (vector properties, older MultiChooseProperty,
//     etc.) is never exposed as a UI row — it silently stays at its paint.net-declared default
//     forever. The plugin still loads; PdnEffectDiscovery lists which parameters aren't
//     controllable in its PluginLoadResult note.
//   - Color pickers are indistinguishable from plain bounded integers in PropertyCollection alone
//     (paint.net only distinguishes them in the WinForms UI layer, which this bridge never walks —
//     see PdnEffectDiscovery for why). Heuristic: a property whose name contains "color"/"colour"
//     (case-insensitive) is treated as an ARGB-packed color; everything else numeric stays a plain
//     slider. Known-weak, not solved further.
//   - A plugin overriding OnCreateConfigUI/CreateConfigDialog for a fully custom, non-property-
//     driven dialog is never asked for that UI at all — it still loads and renders using its
//     declared PropertyCollection in the existing default per-property layout.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KawaPaint.Engine.Plugins.Pdn;

internal static class PdnPropertyMapper
{
    /// <summary>One entry per real paint.net Property. Spec is null for an unmapped property type
    /// (see file header) — such a property is never surfaced in the UI and always renders at
    /// DefaultValue.</summary>
    public sealed class MappedProperty
    {
        public required object PdnProperty { get; init; }
        public required string PdnName { get; init; }
        public required PluginParameterSpec? Spec { get; init; }
        public required object DefaultValue { get; init; }
        public required Type RealValueType { get; init; }
        public object[]? ChoiceValues { get; init; }
        public bool IsColorHeuristic { get; init; }
    }

    public sealed record MapResult(IReadOnlyList<MappedProperty> Properties, IReadOnlyList<string> UnmappedNotes);

    public static MapResult Map(object propertyCollection, PdnReflectionSchema schema)
    {
        var mapped = new List<MappedProperty>();
        var notes = new List<string>();

        foreach (object propObj in (IEnumerable)propertyCollection)
        {
            string name = (string)schema.PropertyName.GetValue(propObj)!;
            object defaultValue = schema.PropertyDefaultValue.GetValue(propObj)!;
            Type realValueType = (Type)schema.PropertyValueType.GetValue(propObj)!;
            Type concreteType = propObj.GetType();
            string typeName = concreteType.Name;

            object[]? choiceValues = null;
            bool isColorHeuristic = false;

            PluginParameterSpec? spec = typeName switch
            {
                "Int32Property" => MapScalar(propObj, name, defaultValue, concreteType, isInt: true, out isColorHeuristic),
                "DoubleProperty" => MapScalar(propObj, name, defaultValue, concreteType, isInt: false, out isColorHeuristic),
                "BooleanProperty" => new BoolParameterSpec(name, Humanize(name), (bool)defaultValue),
                "StaticListChoiceProperty" => MapChoice(propObj, name, defaultValue, concreteType, out choiceValues),
                _ => null
            };

            if (spec is null)
                notes.Add($"'{name}' ({typeName}) is not controllable in this UI — always uses its default.");

            mapped.Add(new MappedProperty
            {
                PdnProperty = propObj,
                PdnName = name,
                Spec = spec,
                DefaultValue = defaultValue,
                RealValueType = realValueType,
                ChoiceValues = choiceValues,
                IsColorHeuristic = isColorHeuristic
            });
        }

        return new MapResult(mapped, notes);
    }

    private static PluginParameterSpec MapScalar(object propObj, string name, object defaultValue, Type concreteType, bool isInt, out bool isColorHeuristic)
    {
        object min = concreteType.GetProperty("MinValue")!.GetValue(propObj)!;
        object max = concreteType.GetProperty("MaxValue")!.GetValue(propObj)!;
        double dMin = Convert.ToDouble(min);
        double dMax = Convert.ToDouble(max);
        double dDefault = Convert.ToDouble(defaultValue);

        isColorHeuristic = isInt && (name.Contains("color", StringComparison.OrdinalIgnoreCase)
                                      || name.Contains("colour", StringComparison.OrdinalIgnoreCase));
        if (isColorHeuristic)
        {
            int packed = Convert.ToInt32(defaultValue);
            var color = ColorBgra.FromBgra(
                (byte)(packed & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)((packed >> 16) & 0xFF), (byte)((packed >> 24) & 0xFF));
            return new ColorParameterSpec(name, Humanize(name), color);
        }

        return new NumericParameterSpec(name, Humanize(name), dMin, dMax, dDefault, isInt ? "0" : "0.00");
    }

    private static PluginParameterSpec MapChoice(object propObj, string name, object defaultValue, Type concreteType, out object[] choiceValues)
    {
        choiceValues = (object[])concreteType.GetProperty("ValueChoices")!.GetValue(propObj)!;
        var options = choiceValues.Select(c => c?.ToString() ?? "").ToList();
        int defaultIndex = Array.IndexOf(choiceValues, defaultValue);
        if (defaultIndex < 0) defaultIndex = 0;
        return new ChoiceParameterSpec(name, Humanize(name), options, defaultIndex);
    }

    /// <summary>"BlurRadius" -&gt; "Blur Radius". Leaves already-spaced or single-word names as-is.</summary>
    private static string Humanize(string propertyName)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < propertyName.Length; i++)
        {
            char c = propertyName[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(propertyName[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
