using KawaPaint.Engine;

// Text + move ops.
using var s = new Surface(360, 160);
s.Clear(ColorBgra.White);
TextOps.DrawText(s, "KawaPaint\nAvalonia + Skia", 20, 20, 44, ColorBgra.FromBgr(40, 40, 220));
BrushOps.FillDisc(s, 300, 110, 30, ColorBgra.FromBgr(220, 120, 20));
s.Save(Path.Combine(AppContext.BaseDirectory, "text_test.png"));
Console.WriteLine("saved text_test.png");

// Move: shift a disc surface by (40,20) and verify the pixel followed.
using var a = new Surface(100, 100);
BrushOps.FillDisc(a, 30, 30, 10, ColorBgra.Black);
using var b = new Surface(100, 100);
SurfaceOps.ShiftInto(b, a, 40, 20);
Console.WriteLine($"moved: src(30,30)={a[30, 30]} -> dst(70,50)={b[70, 50]} (should match); dst(30,30)={b[30, 30]} (should be transparent)");
