// KawaPaint — what a plugin effect looks like to the host: a name, a parameter schema, and a
// factory that turns the dialog's current values into a real IEffect.

using System;
using System.Collections.Generic;

namespace KawaPaint.Engine.Plugins;

public sealed class PluginEffectDescriptor
{
    public PluginEffectDescriptor(string id, string displayName,
        IReadOnlyList<PluginParameterSpec> parameters, Func<PluginParameterValues, IEffect> build,
        string? category = null)
    {
        Id = id;
        DisplayName = displayName;
        Parameters = parameters;
        Build = build;
        Category = category;
    }

    /// <summary>Registry key, deduped by EffectRegistry — by convention "&lt;pluginId&gt;.&lt;effectId&gt;",
    /// not enforced.</summary>
    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>Null groups the effect flat under Plugins ▸ Effects; otherwise a named submenu,
    /// mirroring the built-in Distort/Stylize/Render groupings.</summary>
    public string? Category { get; }

    public IReadOnlyList<PluginParameterSpec> Parameters { get; }
    public Func<PluginParameterValues, IEffect> Build { get; }
}
