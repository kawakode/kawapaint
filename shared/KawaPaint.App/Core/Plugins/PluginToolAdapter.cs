// KawaPaint - wraps a plugin's IPluginTool as a real ITool so it can be assigned to
// Canvas.CurrentTool like any built-in tool. ToolContext and PluginToolContext are structurally
// identical (see IPluginTool.cs's header comment for why they're two separate types); this does
// the field-by-field copy on every pointer event.

using KawaPaint.Engine.Plugins;

namespace KawaPaint.App.Core.Plugins;

public sealed class PluginToolAdapter : ITool
{
    private readonly IPluginTool _inner;

    public PluginToolAdapter(IPluginTool inner) => _inner = inner;

    public string Name => _inner.DisplayName;

    public void PointerDown(ToolContext c) => _inner.PointerDown(Map(c));
    public void PointerMove(ToolContext c) => _inner.PointerMove(Map(c));
    public void PointerUp(ToolContext c) => _inner.PointerUp(Map(c));

    private static PluginToolContext Map(ToolContext c) => new()
    {
        Layer = c.Layer,
        PreStroke = c.PreStroke,
        PrimaryColor = c.PrimaryColor,
        SecondaryColor = c.SecondaryColor,
        BrushWidth = c.BrushWidth,
        Antialias = c.Antialias,
        FillTolerance = c.FillTolerance,
        GlobalFill = c.GlobalFill,
        FillShapes = c.FillShapes,
        CtrlHeld = c.CtrlHeld,
        X = c.X,
        Y = c.Y,
        PushHistory = c.PushHistory,
        Composite = c.Composite,
        SampleComposite = c.SampleComposite,
        SetPrimaryColor = c.SetPrimaryColor,
        Selection = c.Selection,
        SelectionChanged = c.SelectionChanged,
        RequestText = c.RequestText,
        CombineMode = c.CombineMode
    };
}
