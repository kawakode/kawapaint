// KawaPaint — declarative parameter schema for plugin effects. A plugin describes its knobs as
// plain data (this file's types); the host renders them into a generic dialog
// (KawaPaint.App.PluginEffectDialog) without the plugin shipping any UI code of its own. No
// reflection/attributes involved — a plugin author just builds these in code, the same way a
// built-in effect's dialog is described today by AdjustmentDialog.SliderSpec literals.

using System.Collections.Generic;

namespace KawaPaint.Engine.Plugins;

public abstract class PluginParameterSpec
{
    protected PluginParameterSpec(string key, string label)
    {
        Key = key;
        Label = label;
    }

    /// <summary>Looked up via PluginParameterValues.Get*(key) inside the effect factory.</summary>
    public string Key { get; }

    public string Label { get; }
}

public sealed class NumericParameterSpec : PluginParameterSpec
{
    public NumericParameterSpec(string key, string label, double min, double max, double @default, string format = "0.00")
        : base(key, label)
    {
        Min = min;
        Max = max;
        Default = @default;
        Format = format;
    }

    public double Min { get; }
    public double Max { get; }
    public double Default { get; }
    public string Format { get; }
}

public sealed class BoolParameterSpec : PluginParameterSpec
{
    public BoolParameterSpec(string key, string label, bool @default = false) : base(key, label) => Default = @default;

    public bool Default { get; }
}

public sealed class ChoiceParameterSpec : PluginParameterSpec
{
    public ChoiceParameterSpec(string key, string label, IReadOnlyList<string> options, int defaultIndex = 0)
        : base(key, label)
    {
        Options = options;
        DefaultIndex = defaultIndex;
    }

    public IReadOnlyList<string> Options { get; }
    public int DefaultIndex { get; }
}

public sealed class ColorParameterSpec : PluginParameterSpec
{
    public ColorParameterSpec(string key, string label, ColorBgra @default) : base(key, label) => Default = @default;

    public ColorBgra Default { get; }
}

/// <summary>Typed access to a dialog's current parameter values, keyed by PluginParameterSpec.Key.
/// Passed to a PluginEffectDescriptor.Build factory in place of AdjustmentDialog's raw double[],
/// so a plugin effect reads its own parameters by name instead of by positional index.</summary>
public sealed class PluginParameterValues
{
    private readonly Dictionary<string, object> _values;

    /// <summary>Public so the host (KawaPaint.App.PluginEffectDialog, a different assembly) can
    /// build one from the dialog's current control values.</summary>
    public PluginParameterValues(Dictionary<string, object> values) => _values = values;

    public double GetDouble(string key) => (double)_values[key];
    public int GetInt(string key) => (int)System.Math.Round((double)_values[key]);
    public bool GetBool(string key) => (bool)_values[key];
    public int GetChoiceIndex(string key) => (int)_values[key];
    public ColorBgra GetColor(string key) => (ColorBgra)_values[key];
}
