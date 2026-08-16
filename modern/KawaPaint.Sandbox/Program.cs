using KawaPaint.Engine;
using SkiaSharp;

using var s = new Surface(64, 48);
s.Clear(ColorBgra.FromBgr(40, 160, 240));
BrushOps.FillDisc(s, 32, 24, 15, ColorBgra.Black, StampMode.Blend, true);

string dir = AppContext.BaseDirectory;
foreach (var (ext, fmt) in new[] { (".png", SKEncodedImageFormat.Png), (".jpg", SKEncodedImageFormat.Jpeg), (".webp", SKEncodedImageFormat.Webp) })
{
    string p = Path.Combine(dir, "export_test" + ext);
    s.Save(p, fmt, 92);
    using var back = Surface.Load(p);
    Console.WriteLine($"{ext}: {new FileInfo(p).Length} bytes, reloaded {back.Width}x{back.Height}");
}
