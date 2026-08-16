using KawaPaint.Engine;

using var doc = new Document(40, 20);
var l = doc.AddLayer("a");
l.Surface[0, 0] = ColorBgra.FromBgr(0, 0, 255);   // mark top-left red

DocumentOps.FlipHorizontal(doc);
Console.WriteLine($"after flipH: topRight(39,0)={l.Surface[39, 0]} (expect red), topLeft={l.Surface[0, 0]}");

using var rot = DocumentOps.Rotate90(doc, clockwise: true);
Console.WriteLine($"rotated dims = {rot.Width}x{rot.Height} (expect 20x40)");
// red was at (39,0); CW rotation maps (x,y)->(H-1-y, x) = (19, 39)
Console.WriteLine($"rot pixel(19,39)={rot.Layers[0].Surface[19, 39]} (expect red)");
