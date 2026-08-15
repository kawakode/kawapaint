using KawaPaint.Engine;

// Headless test of the tool engine ops: shapes, flood fill, eraser.

using var s = new Surface(400, 300);
s.Clear(ColorBgra.White);

var blue = ColorBgra.FromBgr(220, 60, 20);
var red = ColorBgra.FromBgr(20, 20, 220);
var green = ColorBgr(40, 170, 40);
var yellow = ColorBgra.FromBgr(40, 210, 240);

// Rectangle outline, then flood-fill its interior yellow.
ShapeOps.DrawRectangle(s, 30, 30, 180, 140, 2, blue);
FloodFill.Fill(s, 100, 85, yellow, 0);

// Ellipse outline + a line.
ShapeOps.DrawEllipse(s, 220, 30, 370, 150, 3, red);
BrushOps.DrawLine(s, 30, 200, 370, 260, 4, green);

// Eraser: punch a transparent hole out of the filled rectangle.
BrushOps.FillDisc(s, 105, 90, 22, ColorBgra.Transparent, StampMode.Set);

string outPath = Path.Combine(AppContext.BaseDirectory, "tools_test.png");
s.Save(outPath);
Console.WriteLine($"saved {outPath}");
Console.WriteLine($"fill(60,60)={s[60, 60]} erased(105,90)={s[105, 90]}");

static ColorBgra ColorBgr(byte b, byte g, byte r) => ColorBgra.FromBgr(b, g, r);
