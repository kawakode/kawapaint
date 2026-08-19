using KawaPaint.Engine;
using KawaPaint.Engine.Plugins;

namespace KawaPaint.Plugins.Sample;

/// <summary>Minimal plugin tool: stamps a filled disc of the primary color on every pointer event,
/// reusing the same BrushOps primitives a built-in tool would. Proves IPluginTool/
/// PluginToolContext round-trip through PluginToolAdapter into a real Canvas.CurrentTool.</summary>
internal sealed class GlowDotTool : IPluginTool
{
    public string DisplayName => "Glow Dot (Sample)";

    public void PointerDown(PluginToolContext c)
    {
        c.PushHistory();
        Stamp(c);
    }

    public void PointerMove(PluginToolContext c) => Stamp(c);

    public void PointerUp(PluginToolContext c) { }

    private static void Stamp(PluginToolContext c)
    {
        BrushOps.FillDisc(c.Layer.Surface, c.IX, c.IY, System.Math.Max(4, c.BrushWidth), c.PrimaryColor, StampMode.Blend, c.Antialias);
        c.Composite();
    }
}
