using KawaPaint.Engine;

// Headless engine smoke test: build a Surface with raw pixel ops + alpha blending,
// then save it via SkiaSharp. Proves the engine runs on modern .NET with no Mono/WinForms.

int w = 400, h = 300;
using var surface = new Surface(w, h);

// Background: horizontal B / vertical G gradient, constant R.
unsafe
{
    for (int y = 0; y < h; y++)
    {
        ColorBgra* row = (ColorBgra*)surface.GetRowPointer(y);
        for (int x = 0; x < w; x++)
        {
            row[x] = ColorBgra.FromBgra(
                (byte)(x * 255 / w),
                (byte)(y * 255 / h),
                80,
                255);
        }
    }
}

// Alpha-blend a semi-transparent red square on top using BlendOver.
var red = ColorBgra.FromBgra(0, 0, 220, 128);
for (int y = 60; y < 200; y++)
    for (int x = 80; x < 260; x++)
        surface[x, y] = ColorBgra.BlendOver(surface[x, y], red);

string outPath = Path.Combine(AppContext.BaseDirectory, "engine_test.png");
surface.Save(outPath);
Console.WriteLine($"saved {outPath} ({surface.Width}x{surface.Height})");

// Round-trip: load it back and report a sampled pixel to prove decode works.
using var loaded = Surface.Load(outPath);
var p = loaded[170, 130];
Console.WriteLine($"reloaded {loaded.Width}x{loaded.Height}, pixel(170,130)={p}");
