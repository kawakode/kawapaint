using KawaPaint.Engine;

namespace KawaPaint.Sandbox;

/// <summary>
/// Covers HistoryStack.TruncateFrom, which until the History panel grew a "Delete From Here" row
/// command had no caller at all - the B5 rebase fix was verified only in a throwaway project. The
/// interesting case is not the plain truncate but the one B5 was about: TruncateFrom walks the
/// caret back with JumpTo first, JumpTo ends in Trim(), and Trim can DropOldest() and renumber
/// every surviving step under the index the caller passed in.
/// </summary>
internal static class HistoryTruncateSmokeTest
{
    public static void RunAll()
    {
        TruncatesFromTheEnd();
        TruncateRevertsAppliedSteps();
        TruncateFromZeroDropsEverything();
        OutOfRangeIsNoOp();
        RebasesAcrossATrimThatDropsFromTheFront();
        Console.WriteLine("HISTORY TRUNCATE SMOKE OK - revert, drop-all, out-of-range, drop-count rebase");
    }

    /// <summary>Caret already behind the cut, so no walk and no chance to renumber.</summary>
    private static void TruncatesFromTheEnd()
    {
        using var doc = new Document(32, 32);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack();

        PushPixelEdit(history, layer, 0, ColorBgra.FromBgr(10, 10, 10));
        PushPixelEdit(history, layer, 1, ColorBgra.FromBgr(20, 20, 20));
        PushPixelEdit(history, layer, 2, ColorBgra.FromBgr(30, 30, 30));
        history.JumpTo(1);   // caret behind the step we are about to cut at

        history.TruncateFrom(1);
        Assert(history.Count == 1, $"expected 1 surviving step, got {history.Count}");
        Assert(history.Position == 1, $"caret moved unexpectedly to {history.Position}");
        Assert(!history.CanRedo, "truncated steps are still redoable");
    }

    /// <summary>
    /// The user-visible contract of the row command: cutting at a step that is currently applied
    /// first undoes it, so the pixels go back to what they were before that step.
    /// </summary>
    private static void TruncateRevertsAppliedSteps()
    {
        using var doc = new Document(32, 32);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack();

        PushPixelEdit(history, layer, 0, ColorBgra.FromBgr(10, 10, 10));
        ColorBgra afterFirst = ReadPixel(layer, 0);

        PushPixelEdit(history, layer, 1, ColorBgra.FromBgr(20, 20, 20));
        PushPixelEdit(history, layer, 2, ColorBgra.FromBgr(30, 30, 30));
        Assert(history.Count == 3 && history.Position == 3, "setup did not leave 3 applied steps");

        history.TruncateFrom(1);   // drop steps 2 and 3, reverting them on the way out

        Assert(history.Count == 1, $"expected 1 surviving step, got {history.Count}");
        Assert(history.Position == 1, $"expected caret at 1, got {history.Position}");
        Assert(ReadPixel(layer, 0).Bgra == afterFirst.Bgra, "surviving step's pixels were altered");
        Assert(ReadPixel(layer, 1).A == 0, "truncated step 2 was not reverted");
        Assert(ReadPixel(layer, 2).A == 0, "truncated step 3 was not reverted");
    }

    private static void TruncateFromZeroDropsEverything()
    {
        using var doc = new Document(32, 32);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack();

        for (int i = 0; i < 4; i++) PushPixelEdit(history, layer, i, ColorBgra.FromBgr(50, 50, 50));

        history.TruncateFrom(0);
        Assert(history.Count == 0, $"expected an empty stack, got {history.Count}");
        Assert(history.Position == 0, $"expected caret at 0, got {history.Position}");
        for (int i = 0; i < 4; i++)
            Assert(ReadPixel(layer, i).A == 0, $"edit {i} was not reverted by a full truncate");
    }

    private static void OutOfRangeIsNoOp()
    {
        using var doc = new Document(32, 32);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack();

        PushPixelEdit(history, layer, 0, ColorBgra.FromBgr(10, 10, 10));
        PushPixelEdit(history, layer, 1, ColorBgra.FromBgr(20, 20, 20));

        history.TruncateFrom(2);    // one past the end
        history.TruncateFrom(-1);
        history.TruncateFrom(99);
        Assert(history.Count == 2, $"an out-of-range truncate changed the stack ({history.Count})");
        Assert(history.Position == 2, $"an out-of-range truncate moved the caret to {history.Position}");
    }

    /// <summary>
    /// B5's actual failure shape, using audit #15's own reachable setup: push more steps than the
    /// cap while it is unlimited, then lower MaxSteps, so the Trim() inside TruncateFrom's JumpTo
    /// drops from the FRONT and renumbers everything the caller's index referred to. Without the
    /// _dropCount rebase the cut lands at the wrong place - either sparing steps it should remove
    /// or removing nothing at all.
    /// </summary>
    private static void RebasesAcrossATrimThatDropsFromTheFront()
    {
        using var doc = new Document(32, 32);
        var layer = doc.AddLayer("l");
        var history = new HistoryStack { MemoryBudgetBytes = 0 };   // steps cap only, no byte budget

        for (int i = 0; i < 10; i++) PushPixelEdit(history, layer, i, ColorBgra.FromBgr(60, 60, 60));
        Assert(history.Count == 10, "setup did not push 10 steps");

        history.MaxSteps = 3;       // lowered after the fact, exactly as a live settings change does
        history.TruncateFrom(8);    // walks the caret back 2, which trims 7 steps off the front

        // The walk leaves "edit 7", "edit 8", "edit 9" and a caret of 1, so the cut must land at
        // rebased index 1 and remove the last two. Pixels alone cannot tell the two behaviours
        // apart - JumpTo already reverted those edits either way - and neither can Count<=3, which
        // the buggy path satisfies by doing nothing at all. What separates them is whether the
        // steps are *gone* or merely un-applied: without the rebase, index 8 is past the end of the
        // now-3-step list, TruncateFrom returns early, and both steps stay sitting there redoable.
        Assert(history.Count == 1, $"expected 1 surviving step after the rebased cut, got {history.Count}");
        Assert(!history.CanRedo, "truncated steps are still redoable - the cut landed at a stale index");
        Assert(history.Position == 1, $"expected caret at 1, got {history.Position}");
        Assert(ReadPixel(layer, 8).A == 0, "step 9 was not reverted");
        Assert(ReadPixel(layer, 9).A == 0, "step 10 was not reverted");
    }

    /// <summary>One edit: paint pixel (index,0) opaque, captured as a real tile delta.</summary>
    private static void PushPixelEdit(HistoryStack history, Layer layer, int index, ColorBgra color)
    {
        using Surface before = layer.Surface.Clone();
        SetPixel(layer, index, color);
        history.Push(TileDeltaMemento.Create(layer, before, $"edit {index}"));
    }

    private static unsafe void SetPixel(Layer layer, int index, ColorBgra color)
        => *(ColorBgra*)layer.Surface.GetPointPointer(index, 0) = color;

    private static unsafe ColorBgra ReadPixel(Layer layer, int index)
        => *(ColorBgra*)layer.Surface.GetPointPointer(index, 0);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
