// KawaPaint — the plugin-facing tool contract. This is a deliberate duplicate of
// KawaPaint.App.ToolContext/ITool (field-for-field, method-for-method): plugin contracts live in
// KawaPaint.Engine so a third-party plugin project only needs a reference to the engine, but
// ToolContext itself is an App-side type (it exposes App-only UI callbacks), so Engine cannot
// reference it directly. KawaPaint.App.Core.Plugins.PluginToolAdapter bridges the two at runtime.

using System;

namespace KawaPaint.Engine.Plugins;

public sealed class PluginToolContext
{
    public required Layer Layer { get; init; }
    public required Surface PreStroke { get; init; }
    public required ColorBgra PrimaryColor { get; init; }
    public required ColorBgra SecondaryColor { get; init; }
    public required int BrushWidth { get; init; }
    public required bool Antialias { get; init; }
    public required int FillTolerance { get; init; }
    public required bool GlobalFill { get; init; }
    public required bool FillShapes { get; init; }
    public required bool CtrlHeld { get; init; }

    public double X { get; set; }
    public double Y { get; set; }
    public int IX => (int)Math.Round(X);
    public int IY => (int)Math.Round(Y);

    public required Action PushHistory { get; init; }
    public required Action Composite { get; init; }
    public required Func<int, int, ColorBgra> SampleComposite { get; init; }
    public required Action<ColorBgra> SetPrimaryColor { get; init; }

    public required Selection Selection { get; init; }
    public required Action SelectionChanged { get; init; }
    public required Action<int, int> RequestText { get; init; }
    public required SelectionCombineMode CombineMode { get; init; }
}

public interface IPluginTool
{
    string DisplayName { get; }
    void PointerDown(PluginToolContext c);
    void PointerMove(PluginToolContext c);
    void PointerUp(PluginToolContext c);
}

public sealed class PluginToolDescriptor
{
    public PluginToolDescriptor(string id, string displayName, Func<IPluginTool> create)
    {
        Id = id;
        DisplayName = displayName;
        Create = create;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public Func<IPluginTool> Create { get; }
}
