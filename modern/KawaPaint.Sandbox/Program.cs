using KawaPaint.Engine;

// Round-trip test of the native .kwp layered format.

using var doc = new Document(120, 80);
var bg = doc.AddLayer("Background");
bg.Surface.Clear(ColorBgra.FromBgr(200, 180, 40));
var top = doc.AddLayer("Top");
top.BlendMode = BlendMode.Multiply;
top.Opacity = 170;
BrushOps.FillDisc(top.Surface, 60, 40, 25, ColorBgra.FromBgr(20, 20, 220));

string path = Path.Combine(AppContext.BaseDirectory, "roundtrip.kwp");
DocumentFile.Save(doc, path);
Console.WriteLine($"saved {path} ({new FileInfo(path).Length} bytes)");

using var loaded = DocumentFile.Load(path);
Console.WriteLine($"layers={loaded.LayerCount} " +
    $"[0]={loaded.Layers[0].Name} " +
    $"[1]={loaded.Layers[1].Name}/{loaded.Layers[1].BlendMode}/op={loaded.Layers[1].Opacity}");

bool ok = loaded.LayerCount == 2
          && loaded.Layers[1].BlendMode == BlendMode.Multiply
          && loaded.Layers[1].Opacity == 170
          && loaded.Layers[1].Surface[60, 40] == top.Surface[60, 40]
          && loaded.Layers[0].Surface[5, 5] == bg.Surface[5, 5];
Console.WriteLine($"round-trip intact = {ok}");
