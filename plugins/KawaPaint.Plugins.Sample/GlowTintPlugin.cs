// KawaPaint sample plugin - demonstrates all four PluginParameterSpec kinds (numeric/bool/color;
// see GlowTintEffect for how each one actually reaches the pixel math, not just the dialog).
// A real third-party plugin looks exactly like this: reference only KawaPaint.Engine, implement
// IKawaPaintPlugin, register contributions in Register().

using KawaPaint.Engine;
using KawaPaint.Engine.Plugins;

namespace KawaPaint.Plugins.Sample;

public sealed class GlowTintPlugin : IKawaPaintPlugin
{
    // Must equal the deployed folder name - plugins/GlowTint/GlowTint.dll.
    public string Id => "GlowTint";
    public string DisplayName => "Glow Tint (Sample)";
    public string Version => "1.0.0";

    public void Register(PluginContext context)
    {
        context.RegisterEffect(new PluginEffectDescriptor(
            "GlowTint.tint",
            "Glow Tint",
            new PluginParameterSpec[]
            {
                new NumericParameterSpec("amount", "Amount", 0, 100, 25, "0"),
                new BoolParameterSpec("invert", "Invert", false),
                new ColorParameterSpec("tint", "Tint Color", ColorBgra.FromBgr(0, 180, 255))
            },
            values => new GlowTintEffect(
                values.GetInt("amount"),
                values.GetBool("invert"),
                values.GetColor("tint"))));

        context.RegisterTool(new PluginToolDescriptor(
            "GlowTint.dotStamp", "Glow Dot (Sample)", () => new GlowDotTool()));
    }
}
