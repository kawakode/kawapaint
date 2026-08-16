using KawaPaint.Engine;

// Low-contrast source; original | auto-levels | levels(gamma 0.5)
int tw = 160, th = 120;
using var src = new Surface(tw, th);
unsafe
{
    for (int y = 0; y < th; y++)
    {
        ColorBgra* row = (ColorBgra*)src.GetRowPointer(y);
        for (int x = 0; x < tw; x++)
        {
            byte v = (byte)(70 + (x * 90 / tw));   // compressed 70..160 range
            row[x] = ColorBgra.FromBgr(v, (byte)(70 + y * 90 / th), v);
        }
    }
}

var fx = new (string, IEffect)[] { ("auto", new AutoLevelsEffect()), ("levels g0.5", new LevelsEffect(60, 200, 0.5)) };
using var montage = new Surface(tw * 3, th);
void Blit(Surface s, int c) { for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++) montage[c * tw + x, y] = s[x, y]; }
Blit(src, 0);
for (int i = 0; i < fx.Length; i++) { using var cp = src.Clone(); fx[i].Item2.Apply(cp); Blit(cp, i + 1); Console.WriteLine($"applied {fx[i].Item1}"); }
montage.Save(Path.Combine(AppContext.BaseDirectory, "levels_test.png"));
Console.WriteLine("saved levels_test.png");
