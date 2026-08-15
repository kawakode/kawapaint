using KawaPaint.Engine;

// Headless check of HueSaturationEffect: original | hue+120 | saturation x0 | lightness+0.3

int tw = 160, th = 120;
using var src = new Surface(tw, th);
unsafe
{
    for (int y = 0; y < th; y++)
    {
        ColorBgra* row = (ColorBgra*)src.GetRowPointer(y);
        for (int x = 0; x < tw; x++)
            row[x] = ColorBgra.FromBgr((byte)(x * 255 / tw), (byte)(y * 255 / th),
                                       (byte)(255 - x * 255 / tw));
    }
}

var variants = new (string, IEffect)[]
{
    ("hue+120", new HueSaturationEffect(120, 1, 0)),
    ("desat",   new HueSaturationEffect(0, 0, 0)),
    ("lighten", new HueSaturationEffect(0, 1, 0.3))
};

using var montage = new Surface(tw * 4, th);
void Blit(Surface s, int cx) { for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++) montage[cx * tw + x, y] = s[x, y]; }
Blit(src, 0);
for (int i = 0; i < variants.Length; i++)
{
    using var copy = src.Clone();
    variants[i].Item2.Apply(copy);
    Blit(copy, i + 1);
    Console.WriteLine($"applied {variants[i].Item1}");
}
montage.Save(Path.Combine(AppContext.BaseDirectory, "huesat_test.png"));
Console.WriteLine("saved huesat_test.png");
