using KawaPaint.Engine;

// original | emboss | edge-detect
int tw = 180, th = 140;
using var src = new Surface(tw, th);
unsafe
{
    for (int y = 0; y < th; y++)
    {
        ColorBgra* row = (ColorBgra*)src.GetRowPointer(y);
        for (int x = 0; x < tw; x++)
            row[x] = ColorBgra.FromBgr((byte)(x * 255 / tw), (byte)(y * 255 / th), 120);
    }
}
ShapeOps.DrawEllipse(src, 40, 30, 140, 110, 4, ColorBgra.White);
BrushOps.DrawLine(src, 10, 120, 170, 20, 3, ColorBgra.Black);

var fx = new (string, IEffect)[] { ("emboss", new EmbossEffect()), ("edge", new EdgeDetectEffect()) };
using var montage = new Surface(tw * 3, th);
void Blit(Surface s, int c) { for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++) montage[c * tw + x, y] = s[x, y]; }
Blit(src, 0);
for (int i = 0; i < fx.Length; i++)
{
    using var copy = src.Clone();
    fx[i].Item2.Apply(copy);
    Blit(copy, i + 1);
    Console.WriteLine($"applied {fx[i].Item1}");
}
montage.Save(Path.Combine(AppContext.BaseDirectory, "fx2_test.png"));
Console.WriteLine("saved fx2_test.png");
