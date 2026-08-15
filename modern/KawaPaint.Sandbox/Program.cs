using KawaPaint.Engine;

// Headless test of the layer/document model: two layers with a blend mode, composite,
// plus a history undo/redo round-trip.

using var doc = new Document(300, 200);

// Bottom layer: opaque blue-green gradient.
var bottom = doc.AddLayer("Background");
unsafe
{
    for (int y = 0; y < doc.Height; y++)
    {
        ColorBgra* row = (ColorBgra*)bottom.Surface.GetRowPointer(y);
        for (int x = 0; x < doc.Width; x++)
            row[x] = ColorBgra.FromBgr((byte)(x * 255 / doc.Width), (byte)(y * 255 / doc.Height), 60);
    }
}

// Top layer: a Multiply-blended orange disc at 70% opacity.
var top = doc.AddLayer("Overlay");
top.BlendMode = BlendMode.Multiply;
top.Opacity = 178;
BrushOps.FillDisc(top.Surface, 150, 100, 70, ColorBgra.FromBgr(40, 160, 255)); // orange (BGR)

using (var flat = doc.Flatten())
{
    flat.Save(Path.Combine(AppContext.BaseDirectory, "layers_test.png"));
    Console.WriteLine($"composited {flat.Width}x{flat.Height}, center={flat[150, 100]}");
}

// History round-trip: snapshot the top layer, erase it, undo, redo.
var history = new HistoryStack();
var before = top.Surface[150, 100];
history.Push(new LayerSurfaceMemento(top, "Clear overlay"));
top.Surface.Clear(ColorBgra.Transparent);
var afterClear = top.Surface[150, 100];
history.Undo();
var afterUndo = top.Surface[150, 100];
history.Redo();
var afterRedo = top.Surface[150, 100];

Console.WriteLine($"history: before={before} cleared={afterClear} undo={afterUndo} redo={afterRedo}");
Console.WriteLine($"undo restored = {(before == afterUndo)}, redo re-cleared = {(afterClear == afterRedo)}");
