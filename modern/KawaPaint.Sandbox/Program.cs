using KawaPaint.Engine;

using var s = new Surface(4, 1);
s[0, 0] = ColorBgra.FromBgr(10, 100, 200);

var identity = new byte[256];
var invert = new byte[256];
for (int i = 0; i < 256; i++) { identity[i] = (byte)i; invert[i] = (byte)(255 - i); }

using var a = s.Clone();
new CurvesEffect(identity).Apply(a);
Console.WriteLine($"identity: {a[0, 0]} (expect B=10,G=100,R=200)");

using var b = s.Clone();
new CurvesEffect(invert).Apply(b);
Console.WriteLine($"invert:   {b[0, 0]} (expect B=245,G=155,R=55)");
