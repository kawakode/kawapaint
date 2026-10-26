using KawaPaint.App;
using KawaPaint.Engine;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>Tool regressions from issues #9, #10 and #12.</summary>
public class ToolTests
{
    private static readonly ColorBgra Red = ColorBgra.FromBgra(0, 0, 255, 255);
    private static readonly ColorBgra Blue = ColorBgra.FromBgra(255, 0, 0, 255);

    public static TheoryData<string, int> ShapeTools => new()
    {
        { "Arrow", 3 }, { "Arrow", 20 }, { "Star", 20 }, { "Line", 20 },
        { "Rectangle", 20 }, { "Ellipse", 20 }, { "Rounded Rectangle", 20 },
    };

    /// <summary>Issue #9: dragging a shape around and ending at a point must leave exactly what a
    /// direct drag to that point draws - no pieces of earlier previews.</summary>
    [Theory]
    [MemberData(nameof(ShapeTools))]
    public void ShapePreviewLeavesNoResidue(string tool, int brushWidth)
    {
        using var dragged = new Layer(300, 300);
        Drag(CreateShapeTool(tool), dragged, brushWidth,
            (50, 150), (250, 150), (250, 100), (250, 200), (150, 150), (60, 150));

        using var direct = new Layer(300, 300);
        Drag(CreateShapeTool(tool), direct, brushWidth, (50, 150), (60, 150));

        int stale = 0;
        for (int y = 0; y < 300; y++)
            for (int x = 0; x < 300; x++)
                if (dragged.Surface[x, y] != direct.Surface[x, y]) stale++;
        Assert.Equal(0, stale);
    }

    /// <summary>Issue #12: a color picker click off the image must not change the color.</summary>
    [Theory]
    [InlineData(-5, 10)]
    [InlineData(10, -5)]
    [InlineData(40, 10)]
    [InlineData(10, 40)]
    public void ColorPickerIgnoresClicksOffTheImage(int x, int y)
    {
        using var layer = new Layer(32, 32);
        ColorBgra? picked = null;
        var c = Context(layer, 3, setPrimary: color => picked = color);
        c.X = x; c.Y = y;
        new ColorPickerTool().PointerDown(c);
        Assert.Null(picked);

        c.X = 5; c.Y = 5;
        new ColorPickerTool().PointerDown(c);
        Assert.NotNull(picked);
    }

    /// <summary>Issue #10: with a selection, Move drags the selected pixels (and the selection),
    /// leaves a transparent hole behind and doesn't touch anything else.</summary>
    [Fact]
    public void MoveToolMovesOnlyTheSelectedPixels()
    {
        using var layer = new Layer(40, 20);
        layer.Surface.Clear(Blue);
        for (int y = 5; y < 10; y++)
            for (int x = 5; x < 10; x++)
                layer.Surface[x, y] = Red;
        var selection = new Selection(40, 20);
        selection.ReplaceWithRectangle(5, 5, 10, 10);

        var c = Context(layer, 1, selection: selection);
        var tool = new MoveTool();
        c.X = 7; c.Y = 7;
        tool.PointerDown(c);
        c.X = 27; tool.PointerMove(c);
        tool.PointerUp(c);

        Assert.Equal(Red, layer.Surface[25, 5]);
        Assert.Equal(Red, layer.Surface[29, 9]);
        Assert.Equal(0, layer.Surface[5, 5].A);                 // hole where the pixels were
        Assert.Equal(Blue, layer.Surface[4, 5]);                // outside the selection: untouched
        Assert.Equal(Blue, layer.Surface[24, 5]);
        Assert.Equal(Blue, layer.Surface[30, 9]);
        Assert.True(selection.IsSelected(25, 5) && selection.IsSelected(29, 9), "selection did not move");
        Assert.False(selection.IsSelected(5, 5), "selection stayed behind");
    }

    [Fact]
    public void MoveToolWithoutSelectionStillMovesTheWholeLayer()
    {
        using var layer = new Layer(20, 20);
        layer.Surface[2, 2] = Red;
        var c = Context(layer, 1);
        var tool = new MoveTool();
        tool.PointerDown(c);
        c.X = 5; c.Y = 3; tool.PointerMove(c);
        Assert.Equal(Red, layer.Surface[7, 5]);
        Assert.Equal(0, layer.Surface[2, 2].A);
    }

    private static ITool CreateShapeTool(string name) => name switch
    {
        "Arrow" => new ArrowTool(),
        "Star" => new StarTool(),
        "Line" => new LineTool(),
        "Rectangle" => new RectangleTool(),
        "Ellipse" => new EllipseTool(),
        "Rounded Rectangle" => new RoundedRectangleTool(),
        _ => throw new ArgumentException(name)
    };

    private static void Drag(ITool tool, Layer layer, int brushWidth, params (double X, double Y)[] points)
    {
        var c = Context(layer, brushWidth);
        c.X = points[0].X; c.Y = points[0].Y;
        tool.PointerDown(c);
        foreach (var (x, y) in points.Skip(1)) { c.X = x; c.Y = y; tool.PointerMove(c); }
        tool.PointerUp(c);
        c.PreStroke.Dispose();
    }

    private static ToolContext Context(Layer layer, int brushWidth, Action<ColorBgra>? setPrimary = null,
        Selection? selection = null)
    {
        var pre = layer.Surface.Clone();
        selection ??= new Selection(layer.Width, layer.Height);
        return new ToolContext
        {
            Layer = layer, PreStroke = pre, PrimaryColor = ColorBgra.Black, SecondaryColor = ColorBgra.White,
            BrushWidth = brushWidth, BrushHardness = 1, Antialias = true, FillTolerance = 0, GlobalFill = false,
            FillShapes = false, CtrlHeld = false, PressureResponse = PressureMapping.None,
            PointerKind = ToolPointerKind.Mouse, IsEraser = false, DocumentVersion = 0,
            PushHistory = () => { }, Composite = () => { }, CompositeRect = (_, _, _, _) => { },
            SampleComposite = (x, y) => layer.Surface[Math.Clamp(x, 0, layer.Width - 1), Math.Clamp(y, 0, layer.Height - 1)],
            SetPrimaryColor = setPrimary ?? (_ => { }), ViewScale = 1, InvalidateOverlay = () => { },
            Selection = selection, SelectionChanged = () => { }, RequestText = (_, _) => { },
            RequestDynamicText = (_, _) => { }, CombineMode = SelectionCombineMode.Replace,
        };
    }
}
