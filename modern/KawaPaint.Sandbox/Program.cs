using KawaPaint.Engine;

// Structural layer undo/redo via DelegateMemento.
using var doc = new Document(32, 32);
var a = doc.AddLayer("A");
var history = new HistoryStack();

string Order() => string.Join(",", doc.Layers.Select(l => l.Name));

// Add B
var b = doc.AddLayer("B");
history.Push(new DelegateMemento("Add B", () => doc.RemoveLayer(b), () => doc.AddLayer(b)));
Console.WriteLine($"after add:    {Order()}");

// Reorder: move B (index 1) to bottom (index 0)
doc.MoveLayer(1, 0);
history.Push(new DelegateMemento("Reorder", () => doc.MoveLayer(0, 1), () => doc.MoveLayer(1, 0)));
Console.WriteLine($"after move:   {Order()}");

history.Undo();
Console.WriteLine($"undo move:    {Order()}   (expect A,B)");
history.Undo();
Console.WriteLine($"undo add:     {Order()}   (expect A)");
history.Redo();
Console.WriteLine($"redo add:     {Order()}   (expect A,B)");
history.Redo();
Console.WriteLine($"redo move:    {Order()}   (expect B,A)");

bool ok = Order() == "B,A" && doc.LayerCount == 2;
Console.WriteLine($"structural undo/redo intact = {ok}");
