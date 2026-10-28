using KawaPaint.Engine;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>
/// The undo tree: an edit made after undoing starts a branch instead of destroying the undone
/// steps, and switching branches has to land on exactly the pixels each branch left behind.
/// Pixels are written at (x, 0) and x is spread across tiles, so tile deltas of different
/// steps overlap only where a test means them to.
/// </summary>
public class HistoryBranchTests
{
    private static readonly ColorBgra Red = ColorBgra.FromBgr(0, 0, 255);
    private static readonly ColorBgra Green = ColorBgra.FromBgr(0, 255, 0);
    private static readonly ColorBgra Blue = ColorBgra.FromBgr(255, 0, 0);

    [Fact]
    public void EditAfterUndoKeepsTheUndoneStepsAsABranch()
    {
        using var doc = new Document(256, 8);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack();

        Paint(history, layer, 0, Red, "a");
        Paint(history, layer, 70, Red, "b");
        Paint(history, layer, 140, Red, "c");
        history.JumpTo(1);
        Paint(history, layer, 70, Green, "b'");

        Assert.Equal(2, history.Count);
        Assert.Equal(2, history.Position);
        Assert.False(history.CanRedo);
        Assert.Equal(4, history.TotalCount);
        Assert.Equal(1, history.AlternativesAt(1));
        Assert.Equal(1, history.Steps()[0].Alternatives);

        var branches = history.BranchesAt(1);
        Assert.Equal(2, branches.Count);
        Assert.Equal(("b", 2, false), (branches[0].Name, branches[0].Length, branches[0].IsCurrent));
        Assert.Equal(("b'", 1, true), (branches[1].Name, branches[1].Length, branches[1].IsCurrent));

        history.SwitchBranch(1, 0);
        Assert.Equal(3, history.Count);
        Assert.Equal(3, history.Position);
        Assert.Equal(Red, Read(layer, 70));
        Assert.Equal(Red, Read(layer, 140));
        Assert.Equal(new[] { "a", "b", "c" }, history.Steps().Select(s => s.Name));
        Assert.Equal(1, history.AlternativesAt(1));

        history.SwitchBranch(1, 1);
        Assert.Equal(Green, Read(layer, 70));
        Assert.Equal(0, Read(layer, 140).A);
        Assert.Equal(Red, Read(layer, 0));
    }

    [Fact]
    public void SwitchingBetweenNestedBranchesIsPixelExact()
    {
        using var doc = new Document(256, 8);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack();

        // Same pixel painted on every branch, so each switch has to really undo and redo it.
        Paint(history, layer, 10, Red, "root");          // 1
        Paint(history, layer, 10, Green, "g");           // 2
        Paint(history, layer, 80, Green, "g2");          // 3
        history.Undo();
        Paint(history, layer, 80, Blue, "g2'");          // 3' (nested under g)
        history.JumpTo(1);
        Paint(history, layer, 10, Blue, "b");            // 2' (under root)
        Paint(history, layer, 150, Blue, "b2");
        var onBlue = Snapshot(layer);

        history.SwitchBranch(1, 0);   // over to g, which continues where it was left: g2'
        Assert.Equal(new[] { "root", "g", "g2'" }, history.Steps().Select(s => s.Name));
        Assert.Equal(Green, Read(layer, 10));
        Assert.Equal(Blue, Read(layer, 80));
        Assert.Equal(0, Read(layer, 150).A);
        Assert.Equal(1, history.AlternativesAt(1));   // b
        Assert.Equal(1, history.AlternativesAt(2));   // g2

        history.SwitchBranch(2, 0);   // g2 instead of g2'
        Assert.Equal(Green, Read(layer, 80));

        history.SwitchBranch(1, 1);   // all the way over to b/b2
        Assert.Equal(onBlue, Snapshot(layer));
        Assert.Equal(6, history.TotalCount);
    }

    [Fact]
    public void SwitchingCanStartFromAnyCaretOnTheTimeline()
    {
        using var doc = new Document(256, 8);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack();

        Paint(history, layer, 0, Red, "a");
        history.Undo();
        Paint(history, layer, 0, Green, "a'");
        Paint(history, layer, 70, Green, "b'");
        history.JumpTo(0);   // caret below the fork, nothing applied

        history.SwitchBranch(0, 0);
        Assert.Equal(1, history.Position);
        Assert.Equal(Red, Read(layer, 0));
        Assert.Equal(0, Read(layer, 70).A);
    }

    [Fact]
    public void WithoutKeepBranchesAnEditDiscardsTheRedoSteps()
    {
        using var doc = new Document(256, 8);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack { KeepBranches = false };

        Paint(history, layer, 0, Red, "a");
        Paint(history, layer, 70, Red, "b");
        history.Undo();
        Paint(history, layer, 70, Green, "b'");

        Assert.Equal(2, history.TotalCount);
        Assert.Equal(0, history.AlternativesAt(1));
        Assert.Single(history.BranchesAt(1));
    }

    [Fact]
    public void TruncateKeepsBranchesThatLeaveBeforeTheCut()
    {
        using var doc = new Document(256, 8);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack();

        Paint(history, layer, 0, Red, "a");
        Paint(history, layer, 70, Red, "b");
        history.Undo();
        Paint(history, layer, 70, Green, "b'");
        Paint(history, layer, 140, Green, "c'");
        history.Undo();
        Paint(history, layer, 140, Blue, "c''");   // a branch off b', after the cut below

        history.TruncateFrom(1);   // b' and everything from it, c' included

        Assert.Equal(1, history.Count);
        Assert.Equal(2, history.TotalCount);   // a, and the b branch that left before the cut
        Assert.False(history.CanRedo);
        Assert.Equal(1, history.AlternativesAt(1));
        Assert.Equal(0, Read(layer, 70).A);
        Assert.Equal(0, Read(layer, 140).A);

        history.SwitchBranch(1, 0);
        Assert.Equal(Red, Read(layer, 70));
    }

    [Fact]
    public void StepLimitLetsGoOfAbandonedBranchesFirst()
    {
        using var doc = new Document(256, 8);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack();

        Paint(history, layer, 0, Red, "a");
        Paint(history, layer, 70, Red, "old 1");
        Paint(history, layer, 140, Red, "old 2");
        history.JumpTo(1);
        Paint(history, layer, 70, Green, "new 1");
        Paint(history, layer, 140, Green, "new 2");
        Assert.Equal(5, history.TotalCount);

        history.MaxSteps = 3;
        history.Undo();
        history.Redo();

        Assert.Equal(3, history.TotalCount);
        Assert.Equal(3, history.Count);
        Assert.Equal(0, history.AlternativesAt(1));
        Assert.Equal(new[] { "a", "new 1", "new 2" }, history.Steps().Select(s => s.Name));
    }

    [Fact]
    public void LeastRecentlyUsedBranchGoesFirst()
    {
        using var doc = new Document(256, 8);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack();

        Paint(history, layer, 0, Red, "first");
        history.Undo();
        Paint(history, layer, 0, Green, "second");
        history.Undo();
        Paint(history, layer, 0, Blue, "third");
        history.SwitchBranch(0, 0);   // revisit "first": "second" is now the oldest abandoned one

        history.MaxSteps = 2;
        history.Undo();

        var names = history.BranchesAt(0).Select(b => b.Name).ToArray();
        Assert.Equal(new[] { "first", "third" }, names);
    }

    [Fact]
    public void MemoryBudgetLetsGoOfBranchesBeforeTheTimeline()
    {
        using var doc = new Document(64, 8);
        var history = new HistoryStack { MemoryBudgetBytes = 0 };

        for (int i = 0; i < 8; i++) history.Push(new FixedMemento($"kept {i}", 100));
        history.JumpTo(7);
        history.Push(new FixedMemento("abandons kept 7", 100));
        Assert.Equal(1, history.AlternativesAt(7));

        history.MemoryBudgetBytes = 800;
        history.Push(new FixedMemento("over budget", 0));

        Assert.Equal(0, history.AlternativesAt(7));
        Assert.Equal(9, history.Count);
        Assert.Equal(800, history.ResidentBytes);
    }

    [Fact]
    public void DroppingTheAppliedFrontStepDropsTheBranchesBesideIt()
    {
        var history = new HistoryStack();
        history.Push(new FixedMemento("abandoned", 0));
        history.Undo();
        history.Push(new FixedMemento("front", 0));
        history.Push(new FixedMemento("next", 0));
        Assert.Equal(1, history.AlternativesAt(0));

        history.MaxSteps = 1;
        history.Undo();

        // The abandoned branch went first (it is a branch), then "front" was dropped for good.
        Assert.Equal(1, history.TotalCount);
        Assert.Equal(new[] { "next" }, history.Steps().Select(s => s.Name));
    }

    [Fact]
    public void ResidentBytesStayExactAcrossBranches()
    {
        var history = new HistoryStack { MemoryBudgetBytes = 0 };
        history.Push(new FixedMemento("a", 10));
        history.Push(new FixedMemento("b", 20));
        history.Undo();
        history.Push(new FixedMemento("b'", 5));
        Assert.Equal(35, history.ResidentBytes);

        history.SwitchBranch(1, 0);
        Assert.Equal(35, history.ResidentBytes);
        history.TruncateFrom(1);
        Assert.Equal(15, history.ResidentBytes);   // b gone; b' is a branch off a and stays
        history.Clear();
        Assert.Equal(0, history.ResidentBytes);
        Assert.Equal(0, history.TotalCount);
    }

    /// <summary>
    /// Why DelegateMemento hands its callbacks the applied flag instead of letting them ask the
    /// document. Branch A deletes layer L and is then abandoned for branch B, which deletes L too.
    /// A's step is now on the redo side while L sits detached - by B. Dropping A must not free L:
    /// B's step still needs it to undo.
    /// </summary>
    [Fact]
    public void AbandonedDeleteDoesNotFreeALayerAnotherBranchDetached()
    {
        using var doc = new Document(16, 16);
        doc.AddLayer("base");
        var layer = doc.AddLayer("L");
        var history = new HistoryStack();

        PushDelete(history, doc, layer, "delete on A");
        history.Undo();
        PushDelete(history, doc, layer, "delete on B");
        Assert.False(doc.OwnsLayer(layer));

        history.MaxSteps = 1;   // drops the abandoned A branch
        history.Undo();
        history.Redo();
        Assert.Equal(1, history.TotalCount);

        history.Undo();
        Assert.True(doc.OwnsLayer(layer));
        layer.Surface.Clear(Red);   // throws on a disposed surface
        Assert.Equal(Red, Read(layer, 3));
    }

    [Fact]
    public void DelegateMementoReportsWhetherItIsApplied()
    {
        var seen = new List<bool>();
        var history = new HistoryStack();
        history.Push(new DelegateMemento("x", () => { }, () => { },
            applied => applied ? 7 : 3, applied => seen.Add(applied)));
        Assert.Equal(7, history.ResidentBytes);
        history.Undo();
        Assert.Equal(3, history.ResidentBytes);
        history.Clear();
        Assert.Equal(new[] { false }, seen);
    }

    private static void PushDelete(HistoryStack history, Document doc, Layer layer, string name)
    {
        int index = doc.IndexOf(layer);
        doc.RemoveLayer(layer);
        history.Push(new DelegateMemento(name,
            undo: () => doc.InsertLayer(index, layer),
            redo: () => doc.RemoveLayer(layer),
            approximateBytes: applied => applied ? (long)layer.Surface.Stride * layer.Surface.Height : 0,
            dispose: applied => { if (applied) layer.Dispose(); }));
    }

    private static void Paint(HistoryStack history, Layer layer, int x, ColorBgra color, string name)
    {
        using Surface before = layer.Surface.Clone();
        Write(layer, x, color);
        history.Push(TileDeltaMemento.Create(layer, before, name));
    }

    private static unsafe void Write(Layer layer, int x, ColorBgra color)
        => *(ColorBgra*)layer.Surface.GetPointPointer(x, 0) = color;

    private static unsafe ColorBgra Read(Layer layer, int x)
        => *(ColorBgra*)layer.Surface.GetPointPointer(x, 0);

    private static uint[] Snapshot(Layer layer) =>
        Enumerable.Range(0, layer.Width).Select(x => Read(layer, x).Bgra).ToArray();

    private sealed class FixedMemento(string name, long bytes) : HistoryMemento(name)
    {
        public override long ApproximateBytes => bytes;
        public override HistoryMemento Undo() => this;
    }
}
