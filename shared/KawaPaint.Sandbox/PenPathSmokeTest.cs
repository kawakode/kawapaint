using KawaPaint.App;
using KawaPaint.Engine;

namespace KawaPaint.Sandbox;

/// <summary>
/// The pen tool's path geometry. The interesting claims are all about area: a corner-only path
/// must flatten to exactly the polygon its clicks describe, and a four-node circle drawn with the
/// classic Bezier handles must enclose pi*r*r to well under a percent - which is the assertion
/// that actually proves the cubic evaluator and the subdivision step are right, where "it produced
/// some points" would pass for almost any wrong curve. The rest covers the editing gestures that
/// have no other way to be exercised headlessly: closing on the first anchor, Ctrl-deleting a
/// node, and the zoom-scaled grab radius.
/// </summary>
internal static class PenPathSmokeTest
{
    public static void RunAll()
    {
        CornerPathFlattensToItsPolygon();
        SquareOfCornersCoversItsArea();
        DraggedHandlesAreSymmetric();
        FourNodeCircleCoversPiRSquared();
        ClickingTheFirstAnchorClosesThePath();
        CombineModeIsHonoured();
        CtrlClickRemovesAnAnchor();
        GrabRadiusFollowsTheZoom();
        TooFewPointsEncloseNothing();

        Console.WriteLine("PEN PATH SMOKE OK - area-exact polygon/circle, handle symmetry, close/edit gestures");
    }

    /// <summary>Four plain clicks are four polygon vertices - no interior points invented for a
    /// straight edge, and no duplicate of the start point left by the wrap-around segment.</summary>
    private static void CornerPathFlattensToItsPolygon()
    {
        var pen = new PenTool();
        var context = Context();
        ClickCorner(pen, context, 10, 10);
        ClickCorner(pen, context, 50, 10);
        ClickCorner(pen, context, 50, 50);
        ClickCorner(pen, context, 10, 50);

        var outline = pen.Flatten();
        Assert(outline.Count == 4, $"a four-corner path flattened to {outline.Count} points, expected 4");
        Assert(outline[0] == (10.0, 10.0) && outline[2] == (50.0, 50.0),
            "the flattened corners are not the points that were clicked");
    }

    private static void SquareOfCornersCoversItsArea()
    {
        var pen = new PenTool();
        var context = Context();
        ClickCorner(pen, context, 10, 10);
        ClickCorner(pen, context, 50, 10);
        ClickCorner(pen, context, 50, 50);
        ClickCorner(pen, context, 10, 50);

        var selection = new Selection(100, 100);
        Assert(pen.ApplyAsSelection(selection, SelectionCombineMode.Replace, antialias: true),
            "a four-corner path refused to close");
        Assert(!pen.HasPath, "closing the path left it live on the tool");

        double covered = TotalCoverage(selection);
        Assert(Math.Abs(covered - 1600) < 10, $"a 40x40 pen square covers {covered:0.##}, expected 1600");
    }

    /// <summary>Dragging out of a fresh click mirrors the handle through the anchor - that mirror
    /// is the whole reason a curve stays smooth across the node rather than kinking at it.</summary>
    private static void DraggedHandlesAreSymmetric()
    {
        var pen = new PenTool();
        var context = Context();
        DragNode(pen, context, 30, 30, 40, 20);

        PenAnchor node = pen.Anchors[0];
        Assert(node.OutX == 40 && node.OutY == 20, "the dragged handle did not follow the pointer");
        Assert(node.InX == 20 && node.InY == 40, "the opposite handle was not mirrored through the anchor");
        Assert(!node.IsCorner, "a node with handles still reports itself as a corner");
    }

    /// <summary>
    /// The standard four-cubic circle: handles of 0.5523r along each tangent. Its enclosed area is
    /// pi*r*r to about 0.03%, so anything meaningfully off means the cubic evaluator, the handle
    /// mirroring or the subdivision density is wrong.
    /// </summary>
    private static void FourNodeCircleCoversPiRSquared()
    {
        const double cx = 50, cy = 50, r = 20;
        const double k = 0.5522847498 * r;

        var pen = new PenTool();
        var context = Context();
        // Clockwise from the top; each drag pulls the outgoing handle along the tangent.
        DragNode(pen, context, cx, cy - r, cx + k, cy - r);
        DragNode(pen, context, cx + r, cy, cx + r, cy + k);
        DragNode(pen, context, cx, cy + r, cx - k, cy + r);
        DragNode(pen, context, cx - r, cy, cx - r, cy - k);

        var selection = new Selection(100, 100);
        Assert(pen.ApplyAsSelection(selection, SelectionCombineMode.Replace, antialias: true),
            "the circle path refused to close");

        double expected = Math.PI * r * r;
        double covered = TotalCoverage(selection);
        Assert(Math.Abs(covered - expected) / expected < 0.01,
            $"the four-node pen circle covers {covered:0.##}, expected about {expected:0.##}");
        Assert(selection.CoverageAt((int)cx, (int)cy) == 255, "the centre of the pen circle is not selected");
        Assert(selection.CoverageAt(0, 0) == 0, "a corner outside the pen circle is selected");
    }

    /// <summary>Clicking back on the first anchor is the mouse-only way to finish, so it has to do
    /// the same thing the Enter key does: rasterize the outline and drop the path.</summary>
    private static void ClickingTheFirstAnchorClosesThePath()
    {
        var pen = new PenTool();
        var selection = new Selection(100, 100);
        var context = Context(selection);
        ClickCorner(pen, context, 20, 20);
        ClickCorner(pen, context, 70, 20);
        ClickCorner(pen, context, 70, 70);

        ClickCorner(pen, context, 20, 20);   // back onto the first anchor

        Assert(!pen.HasPath, "clicking the first anchor did not close the path");
        Assert(selection.IsActive, "closing the path left no selection behind");
        double covered = TotalCoverage(selection);
        Assert(Math.Abs(covered - 1250) < 20, $"the closed triangle covers {covered:0.##}, expected about 1250");
    }

    /// <summary>The pen goes through the same replace/add/subtract/intersect path as the other
    /// selection tools, so a subtract has to bite a hole out of what was already selected.</summary>
    private static void CombineModeIsHonoured()
    {
        var selection = new Selection(100, 100);
        selection.SelectAll();

        var pen = new PenTool();
        var context = Context(selection);
        ClickCorner(pen, context, 10, 10);
        ClickCorner(pen, context, 50, 10);
        ClickCorner(pen, context, 50, 50);
        ClickCorner(pen, context, 10, 50);
        pen.ApplyAsSelection(selection, SelectionCombineMode.Subtract, antialias: true);

        double covered = TotalCoverage(selection);
        Assert(Math.Abs(covered - (10000 - 1600)) < 10,
            $"subtracting a 40x40 pen square from the whole canvas left {covered:0.##}, expected 8400");
        Assert(selection.CoverageAt(30, 30) == 0, "the subtracted square is still selected");
    }

    private static void CtrlClickRemovesAnAnchor()
    {
        var pen = new PenTool();
        var context = Context();
        ClickCorner(pen, context, 10, 10);
        ClickCorner(pen, context, 50, 10);
        ClickCorner(pen, context, 50, 50);
        Assert(pen.Anchors.Count == 3, "three clicks did not place three anchors");

        ClickCorner(pen, context, 50, 10, ctrl: true);
        Assert(pen.Anchors.Count == 2, $"Ctrl-click left {pen.Anchors.Count} anchors, expected 2");
        Assert(pen.Anchors[1].X == 50 && pen.Anchors[1].Y == 50, "Ctrl-click removed the wrong anchor");
    }

    /// <summary>
    /// The grab radius is in screen pixels, scaled into image space by the view. Zoomed out to
    /// 10% a node covers a tenth of an image pixel, and without the scaling the path would become
    /// impossible to close by clicking; this is what proves the tool reads ViewScale at all.
    /// </summary>
    private static void GrabRadiusFollowsTheZoom()
    {
        // Spread wide enough that no click lands inside another node's radius at either zoom.
        var pen = new PenTool();
        var near = Context();
        ClickCorner(pen, near, 10, 10);
        ClickCorner(pen, near, 200, 10);
        ClickCorner(pen, near, 200, 200);

        // 60 image pixels from the start: far outside the 7px radius at 1:1, inside the 70px one
        // the same 7 screen pixels cover at 10% zoom.
        ClickCorner(pen, near, 70, 10);
        Assert(pen.HasPath && pen.Anchors.Count == 4,
            "a click 60px from the first anchor closed the path at 1:1 zoom");

        var pen2 = new PenTool();
        var far = Context(viewScale: 10);
        ClickCorner(pen2, far, 10, 10);
        ClickCorner(pen2, far, 200, 10);
        ClickCorner(pen2, far, 200, 200);
        ClickCorner(pen2, far, 70, 10);
        Assert(!pen2.HasPath, "the same click did not close the path zoomed out to 10%");
    }

    private static void TooFewPointsEncloseNothing()
    {
        var pen = new PenTool();
        var context = Context();
        ClickCorner(pen, context, 10, 10);
        ClickCorner(pen, context, 50, 50);

        var selection = new Selection(100, 100);
        Assert(!pen.ApplyAsSelection(selection, SelectionCombineMode.Replace, antialias: true),
            "a two-point path claimed to enclose an area");
        Assert(!selection.IsActive, "a rejected path still touched the selection");
        Assert(pen.HasPath, "a rejected path was thrown away rather than left to finish");
    }

    /// <summary>A plain click: press and release without moving, which is how a corner is placed.</summary>
    private static void ClickCorner(PenTool pen, ToolContext context, double x, double y, bool ctrl = false)
    {
        // CtrlHeld is init-only on the context, so a Ctrl-click needs its own.
        ToolContext c = ctrl ? Context(context.Selection, context.ViewScale, ctrl: true) : context;
        c.X = x; c.Y = y;
        pen.PointerDown(c);
        pen.PointerUp(c);
    }

    /// <summary>A click-drag: press on the anchor, pull the outgoing handle to (hx,hy), release.</summary>
    private static void DragNode(PenTool pen, ToolContext context, double x, double y, double hx, double hy)
    {
        context.X = x; context.Y = y;
        pen.PointerDown(context);
        context.X = hx; context.Y = hy;
        pen.PointerMove(context);
        pen.PointerUp(context);
    }

    private static ToolContext Context(Selection? selection = null, double viewScale = 1, bool ctrl = false)
    {
        var layer = new Layer(100, 100, "pen");
        return new ToolContext
        {
            Layer = layer,
            PreStroke = layer.Surface.Clone(),
            PrimaryColor = ColorBgra.Black,
            SecondaryColor = ColorBgra.White,
            BrushWidth = 1,
            BrushHardness = 1,
            Antialias = true,
            FillTolerance = 0,
            GlobalFill = false,
            FillShapes = false,
            CtrlHeld = ctrl,
            PressureResponse = PressureMapping.None,
            PointerKind = ToolPointerKind.Mouse,
            IsEraser = false,
            DocumentVersion = 1,
            PushHistory = () => { },
            Composite = () => { },
            CompositeRect = (_, _, _, _) => { },
            SampleComposite = (_, _) => ColorBgra.Transparent,
            SetPrimaryColor = _ => { },
            ViewScale = viewScale,
            InvalidateOverlay = () => { },
            Selection = selection ?? new Selection(100, 100),
            SelectionChanged = () => { },
            RequestText = (_, _) => { },
            RequestDynamicText = (_, _) => { },
            CombineMode = SelectionCombineMode.Replace
        };
    }

    private static double TotalCoverage(Selection selection)
    {
        double total = 0;
        foreach (byte b in selection.Mask) total += b / 255.0;
        return total;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Pen path smoke test: " + message);
    }
}
