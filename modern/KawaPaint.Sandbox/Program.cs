using KawaPaint.Engine;

// Headless test of selection clipping: an effect applied only inside an ellipse selection.

int w = 300, h = 200;
using var s = new Surface(w, h);
unsafe
{
    for (int y = 0; y < h; y++)
    {
        ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
        for (int x = 0; x < w; x++)
            row[x] = ColorBgra.FromBgr((byte)(x * 255 / w), (byte)(y * 255 / h), 128);
    }
}

var sel = new Selection(w, h);
sel.ReplaceWithEllipse(60, 30, 240, 170);

using var snapshot = s.Clone();
new InvertEffect().Apply(s);        // invert everything...
sel.Clip(s, snapshot);              // ...then restore outside the ellipse

s.Save(Path.Combine(AppContext.BaseDirectory, "selection_test.png"));

var inside = s[150, 100];
var outside = s[10, 10];
var origInside = snapshot[150, 100];
Console.WriteLine($"inside inverted = {inside != origInside} ({inside}) ; outside untouched = {outside == snapshot[10, 10]}");
Console.WriteLine("saved selection_test.png");
