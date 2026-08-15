using KawaPaint.Engine;

// Headless test of effects: apply each to a copy of a colorful source and tile into a montage.

int tw = 200, th = 150;
using var source = new Surface(tw, th);
unsafe
{
    for (int y = 0; y < th; y++)
    {
        ColorBgra* row = (ColorBgra*)source.GetRowPointer(y);
        for (int x = 0; x < tw; x++)
            row[x] = ColorBgra.FromBgr((byte)(x * 255 / tw), (byte)(y * 255 / th),
                                       (byte)((x + y) * 255 / (tw + th)));
    }
}
// a couple of shapes so blur/sharpen are visible
ShapeOps.DrawEllipse(source, 60, 40, 140, 110, 3, ColorBgra.White);
BrushOps.DrawLine(source, 10, 130, 190, 20, 4, ColorBgra.Black);

var effects = new IEffect[]
{
    new InvertEffect(), new GrayscaleEffect(), new SepiaEffect(),
    new BrightnessContrastEffect(40, 1.0), new BrightnessContrastEffect(0, 1.6),
    new BoxBlurEffect(6), new SharpenEffect()
};

int cols = 4, rows = 2;
using var montage = new Surface(tw * cols, th * rows);
montage.Clear(ColorBgra.FromBgr(30, 30, 30));

// Cell 0 = original, then each effect.
void Blit(Surface src, int cx, int cy)
{
    for (int y = 0; y < th; y++)
        for (int x = 0; x < tw; x++)
            montage[cx * tw + x, cy * th + y] = src[x, y];
}
Blit(source, 0, 0);
for (int i = 0; i < effects.Length; i++)
{
    using var copy = source.Clone();
    effects[i].Apply(copy);
    int idx = i + 1;
    Blit(copy, idx % cols, idx / cols);
    Console.WriteLine($"applied {effects[i].Name}");
}

string outPath = Path.Combine(AppContext.BaseDirectory, "effects_test.png");
montage.Save(outPath);
Console.WriteLine($"saved {outPath}");
