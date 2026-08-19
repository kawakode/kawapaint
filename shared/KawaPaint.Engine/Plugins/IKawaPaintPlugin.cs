// KawaPaint — the plugin entry point. A plugin assembly exposes exactly one implementation of
// this interface; PluginManager instantiates it and calls Register once at load time.

using System.Collections.Generic;

namespace KawaPaint.Engine.Plugins;

public interface IKawaPaintPlugin
{
    /// <summary>Stable identifier. Must equal the plugin's containing folder name — see
    /// PluginManager, which checks this before any contribution is kept.</summary>
    string Id { get; }

    string DisplayName { get; }
    string Version { get; }

    /// <summary>
    /// Called once, immediately after this instance is constructed. Register everything the
    /// plugin contributes via <paramref name="context"/>. Any exception thrown here is caught by
    /// PluginManager and reported as a failed load — nothing registered on this call survives.
    /// </summary>
    void Register(PluginContext context);
}

/// <summary>
/// Buffers a plugin's registrations during <see cref="IKawaPaintPlugin.Register"/> instead of
/// writing straight into the live registries, so a plugin that throws partway through leaves
/// nothing behind — PluginManager only copies the buffered lists into EffectRegistry/ToolRegistry
/// once Register() returns without throwing.
/// </summary>
public sealed class PluginContext
{
    internal readonly List<PluginEffectDescriptor> Effects = new();
    internal readonly List<PluginToolDescriptor> Tools = new();

    public void RegisterEffect(PluginEffectDescriptor descriptor) => Effects.Add(descriptor);
    public void RegisterTool(PluginToolDescriptor descriptor) => Tools.Add(descriptor);
}
