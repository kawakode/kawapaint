// Test fixture only — verifies PluginManager's buffered/atomic registration: this plugin
// registers one effect, then throws. The load must be reported Failed and
// "ThrowingFixture.neverRegistered" must never appear in EffectRegistry.All.

using KawaPaint.Engine;
using KawaPaint.Engine.Plugins;

namespace KawaPaint.Plugins.ThrowingFixture;

public sealed class ThrowingFixturePlugin : IKawaPaintPlugin
{
    public string Id => "ThrowingFixture";
    public string DisplayName => "Throwing Fixture (test)";
    public string Version => "1.0.0";

    public void Register(PluginContext context)
    {
        context.RegisterEffect(new PluginEffectDescriptor(
            "ThrowingFixture.neverRegistered",
            "Never Registered",
            System.Array.Empty<PluginParameterSpec>(),
            _ => new InvertEffect()));

        throw new System.InvalidOperationException("Deliberate failure for PluginManager rollback verification.");
    }
}
