// KawaPaint - plugin-contributed effects, separate from the 41 built-in effects (which stay on
// their existing hardcoded switch in MainView.axaml.cs - this registry is additive, not a
// replacement). Mirrors CodecRegistry's dedup-by-id/later-wins shape.

using System;
using System.Collections.Generic;
using System.Linq;

namespace KawaPaint.Engine.Plugins;

public static class EffectRegistry
{
    private static readonly List<PluginEffectDescriptor> _effects = new();

    public static event EventHandler? Changed;

    /// <summary>Adds an effect, replacing any existing one with the same id. Later registrations win.</summary>
    public static void Register(PluginEffectDescriptor descriptor)
    {
        _effects.RemoveAll(e => string.Equals(e.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
        _effects.Add(descriptor);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static IReadOnlyList<PluginEffectDescriptor> All => _effects;

    public static PluginEffectDescriptor? FindById(string id)
        => _effects.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Drops everything registered so far. Safe to call unconditionally - built-in
    /// effects never touch this registry. Used by a plugin reload to start clean.</summary>
    public static void Clear()
    {
        _effects.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
