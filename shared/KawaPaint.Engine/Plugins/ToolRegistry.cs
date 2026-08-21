// KawaPaint - plugin-contributed tools, additive alongside the existing hardcoded tool switch in
// MainView.axaml.cs. Structural twin of EffectRegistry.

using System;
using System.Collections.Generic;
using System.Linq;

namespace KawaPaint.Engine.Plugins;

public static class ToolRegistry
{
    private static readonly List<PluginToolDescriptor> _tools = new();

    public static event EventHandler? Changed;

    public static void Register(PluginToolDescriptor descriptor)
    {
        _tools.RemoveAll(t => string.Equals(t.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
        _tools.Add(descriptor);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static IReadOnlyList<PluginToolDescriptor> All => _tools;

    public static bool TryGet(string id, out PluginToolDescriptor descriptor)
    {
        var found = _tools.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        descriptor = found!;
        return found is not null;
    }

    public static void Clear()
    {
        _tools.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
