using KawaPaint.Engine;

// Headless engine smoke test: raw pixel ops + alpha blend, plus BrushOps stroke rasterization.
// Proves the engine (and the pencil tool's engine side) runs on modern .NET, no Mono/WinForms.

int w = 400, h = 300;
using var surface = new Surface(w, h);

unsafe
{
    for (int y = 0; y < h; y++)
    {
        ColorBgra* row = (ColorBgra*)surface.GetRowPointer(y);
        for (int x = 0; x < w; x++)
            row[x] = ColorBgra.FromBgra((byte)(x * 255 / w), (byte)(y * 255 / h), 80, 255);
    }
}

var red = ColorBgra.FromBgra(0, 0, 220, 128);
for (int y = 60; y < 200; y++)
    for (int x = 80; x < 260; x++)
        surface[x, y] = ColorBgra.BlendOver(surface[x, y], red);

string outPath = Path.Combine(AppContext.BaseDirectory, "engine_test.png");
surface.Save(outPath);
Console.WriteLine($"saved {outPath} ({surface.Width}x{surface.Height})");

using var loaded = Surface.Load(outPath);
Console.WriteLine($"reloaded {loaded.Width}x{loaded.Height}, pixel(170,130)={loaded[170, 130]}");

// --- BrushOps: strokes on a blank white canvas ---
using var canvas = new Surface(400, 300);
canvas.Clear(ColorBgra.White);
BrushOps.DrawLine(canvas, 30, 30, 370, 90, 6, ColorBgra.FromBgr(20, 20, 220));   // thick red
BrushOps.DrawLine(canvas, 30, 120, 370, 200, 2, ColorBgra.FromBgr(220, 40, 40)); // thin blue
BrushOps.DrawLine(canvas, 40, 260, 360, 260, 14, ColorBgra.FromBgra(40, 180, 40, 128)); // fat translucent green
BrushOps.FillDisc(canvas, 200, 150, 40, ColorBgra.FromBgra(0, 0, 0, 90));         // soft-ish black blob
string brushPath = Path.Combine(AppContext.BaseDirectory, "brush_test.png");
canvas.Save(brushPath);
Console.WriteLine($"saved {brushPath}");
