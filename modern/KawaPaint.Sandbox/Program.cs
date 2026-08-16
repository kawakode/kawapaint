using KawaPaint.Engine;

using var s = new Surface(200, 100);
s.Clear(ColorBgra.White);
var blk = ColorBgra.Black;
BrushOps.FillDisc(s, 50, 50, 30, blk, StampMode.Blend, antialias: false);  // hard
BrushOps.FillDisc(s, 150, 50, 30, blk, StampMode.Blend, antialias: true);   // soft
BrushOps.DrawLine(s, 10, 90, 190, 95, 2, ColorBgra.FromBgr(0, 0, 220), StampMode.Blend, antialias: true);
s.Save(Path.Combine(AppContext.BaseDirectory, "aa_test.png"));

// edge pixel of AA disc should be partial alpha (blended toward white), hard disc should be solid.
Console.WriteLine($"hard edge(50,20)={s[50, 20]}  aa edge(150,20)={s[150, 20]}");
Console.WriteLine("saved aa_test.png");
