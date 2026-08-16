using KawaPaint.Engine;

using var doc = new Document(200, 100);
doc.AddLayer("a").Surface.Clear(ColorBgra.FromBgr(30, 160, 220));
BrushOps.FillDisc(doc.Layers[0].Surface, 100, 50, 30, ColorBgra.Black);

using var resized = DocumentOps.Resize(doc, 100, 50);   // half size
Console.WriteLine($"resized doc = {resized.Width}x{resized.Height}, layers={resized.LayerCount}");
resized.Flatten().Save(Path.Combine(AppContext.BaseDirectory, "resize_test.png"));

bool ok = resized.Width == 100 && resized.Height == 50 && resized.LayerCount == 1;
Console.WriteLine($"resize ok = {ok}, center={resized.Layers[0].Surface[50, 25]}");
