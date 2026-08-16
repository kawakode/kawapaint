using KawaPaint.Engine;

int tw = 160, th = 120;
using var src = new Surface(tw, th);
unsafe
{
    for (int y = 0; y < th; y++)
    {
        ColorBgra* row = (ColorBgra*)src.GetRowPointer(y);
        for (int x = 0; x < tw; x++)
            row[x] = ColorBgra.FromBgr((byte)(x * 255 / tw), (byte)(y * 255 / th), 150);
    }
}
var fx = new (string, IEffect)[] { ("posterize4", new PosterizeEffect(4)), ("noise40", new NoiseEffect(40)) };
using var montage = new Surface(tw * 3, th);
void Blit(Surface s, int c) { for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++) montage[c * tw + x, y] = s[x, y]; }
Blit(src, 0);
for (int i = 0; i < fx.Length; i++) { using var cp = src.Clone(); fx[i].Item2.Apply(cp); Blit(cp, i + 1); Console.WriteLine($"applied {fx[i].Item1}"); }
montage.Save(Path.Combine(AppContext.BaseDirectory, "posternoise_test.png"));
Console.WriteLine("saved posternoise_test.png");
