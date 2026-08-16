using KawaPaint.Engine;

using var doc = new Document(20, 20);
var below = doc.AddLayer("below");
below.Surface.Clear(ColorBgra.FromBgr(0, 0, 200));       // red
var above = doc.AddLayer("above");
above.Opacity = 128;
above.Surface.Clear(ColorBgra.FromBgr(200, 0, 0));        // blue @ 50%

var expected = Blending.Composite(above.BlendMode, below.Surface[5, 5], above.Surface[5, 5], above.Opacity);
LayerOps.MergeInto(below, above);
Console.WriteLine($"merged pixel = {below.Surface[5, 5]}  expected = {expected}  match = {below.Surface[5, 5] == expected}");

var dup = below.Clone();
Console.WriteLine($"clone name='{dup.Name}' same-pixels={dup.Surface[5, 5] == below.Surface[5, 5]}");
