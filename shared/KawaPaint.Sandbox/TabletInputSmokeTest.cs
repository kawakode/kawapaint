using KawaPaint.App;
using KawaPaint.Engine;

namespace KawaPaint.Sandbox;

internal static class TabletInputSmokeTest
{
    public static void RunAll()
    {
        int lowSize = DrawPencil(PressureMapping.Size, 0.2, out _);
        int fullSize = DrawPencil(PressureMapping.Size, 1, out _);
        Assert(lowSize > 0 && fullSize > lowSize * 3,
            $"size pressure did not widen the stroke ({lowSize} vs {fullSize} pixels)");

        DrawPencil(PressureMapping.Opacity, 0.25, out byte lowAlpha);
        DrawPencil(PressureMapping.Opacity, 1, out byte fullAlpha);
        Assert(lowAlpha is >= 60 and <= 68 && fullAlpha == 255,
            $"opacity pressure did not scale alpha ({lowAlpha} vs {fullAlpha})");

        using var layer = new Layer(32, 32, "erase");
        layer.Surface.Clear(ColorBgra.FromBgra(40, 80, 120, 200));
        using var before = layer.Surface.Clone();
        var eraser = new EraserTool();
        ToolContext erase = Context(layer, before, PressureMapping.Opacity, 0.5);
        eraser.PointerDown(erase);
        eraser.PointerUp(erase);
        byte erasedAlpha = layer.Surface[16, 16].A;
        Assert(erasedAlpha is >= 98 and <= 102,
            $"pressure eraser should halve alpha, got {erasedAlpha}");

        Console.WriteLine("TABLET INPUT SMOKE OK - pressure size/opacity + partial eraser");
    }

    private static int DrawPencil(PressureMapping mapping, double pressure, out byte centerAlpha)
    {
        using var layer = new Layer(32, 32, "pressure");
        using var before = layer.Surface.Clone();
        var pencil = new PencilTool();
        ToolContext context = Context(layer, before, mapping, pressure);
        pencil.PointerDown(context);
        pencil.PointerUp(context);

        int count = 0;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                if (layer.Surface[x, y].A != 0) count++;
        centerAlpha = layer.Surface[16, 16].A;
        return count;
    }

    private static ToolContext Context(Layer layer, Surface before, PressureMapping mapping, double pressure)
        => new()
        {
            Layer = layer,
            PreStroke = before,
            PrimaryColor = ColorBgra.Black,
            SecondaryColor = ColorBgra.White,
            BrushWidth = 20,
            BrushHardness = 1,
            Antialias = true,
            FillTolerance = 0,
            GlobalFill = false,
            FillShapes = false,
            CtrlHeld = false,
            PressureResponse = mapping,
            PointerKind = ToolPointerKind.Pen,
            IsEraser = false,
            DocumentVersion = 1,
            X = 16,
            Y = 16,
            Pressure = pressure,
            PushHistory = () => { },
            Composite = () => { },
            CompositeRect = (_, _, _, _) => { },
            SampleComposite = (_, _) => ColorBgra.Transparent,
            SetPrimaryColor = _ => { },
            Selection = new Selection(32, 32),
            SelectionChanged = () => { },
            RequestText = (_, _) => { },
            RequestDynamicText = (_, _) => { },
            CombineMode = SelectionCombineMode.Replace
        };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Tablet input smoke test: " + message);
    }
}
