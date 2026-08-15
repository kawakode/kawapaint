using KawaPaint.Engine;

using var doc = new Document(200, 150);
doc.AddLayer("bg").Surface.Clear(ColorBgra.FromBgr(200, 200, 60));
var top = doc.AddLayer("top");
BrushOps.FillDisc(top.Surface, 100, 75, 40, ColorBgra.FromBgr(20, 20, 220));

// Crop to a selection.
var sel = new Selection(200, 150);
sel.ReplaceWithRectangle(60, 40, 160, 120);
var (bx, by, bw, bh) = sel.GetBounds();
Console.WriteLine($"bounds = {bx},{by} {bw}x{bh}");

using var cropped = DocumentOps.Crop(doc, bx, by, bw, bh);
Console.WriteLine($"cropped doc = {cropped.Width}x{cropped.Height}, layers={cropped.LayerCount}");
cropped.Flatten().Save(Path.Combine(AppContext.BaseDirectory, "crop_test.png"));

using var flat = DocumentOps.Flatten(doc);
Console.WriteLine($"flattened layers = {flat.LayerCount} (expect 1)");

bool ok = cropped.Width == bw && cropped.Height == bh && cropped.LayerCount == 2 && flat.LayerCount == 1;
Console.WriteLine($"crop+flatten ok = {ok}");
