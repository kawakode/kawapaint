using KawaPaint.Engine;
using Xunit;

namespace KawaPaint.Tests;

public class SelectionHistoryTests
{
    [Fact]
    public void SnapshotRestoresExactlyAndStaysSmall()
    {
        var selection = new Selection(4000, 3000);
        selection.ReplaceWithEllipse(100, 200, 3100, 2600, antialias: true);
        var snapshot = SelectionSnapshot.Capture(selection);
        byte[] expected = selection.Mask.ToArray();

        selection.SelectAll();
        Assert.False(snapshot.Matches(selection));
        Assert.True(snapshot.RestoreTo(selection));
        Assert.True(selection.IsActive);
        Assert.True(selection.Mask.SequenceEqual(expected));
        Assert.True(snapshot.Matches(selection));
        Assert.True(snapshot.ApproximateBytes < 200_000, $"{snapshot.ApproximateBytes} bytes for an ellipse");
    }

    [Fact]
    public void MementoUndoesAndRedoesThroughTheStack()
    {
        var selection = new Selection(64, 64);
        int notified = 0;
        var history = new HistoryStack();

        var before = SelectionSnapshot.Capture(selection);
        selection.ReplaceWithRectangle(4, 4, 20, 20);
        history.Push(SelectionMemento.Create("Rectangle Select", before, () => selection, () => notified++));
        before = SelectionSnapshot.Capture(selection);
        selection.Invert();
        history.Push(SelectionMemento.Create("Invert Selection", before, () => selection, () => notified++));

        history.Undo();
        Assert.True(selection.IsSelected(10, 10) && !selection.IsSelected(40, 40));
        Assert.Equal((0, 0, 0, 0), history.LastChangedArea);
        history.Undo();
        Assert.False(selection.IsActive);
        history.Redo();
        history.Redo();
        Assert.True(selection.IsSelected(40, 40) && !selection.IsSelected(10, 10));
        Assert.Equal(4, notified);

        // No change, no step.
        Assert.Null(SelectionMemento.Create("Nothing", SelectionSnapshot.Capture(selection), () => selection, () => { }));
    }

    [Fact]
    public void StepForAnotherCanvasSizeDoesNothing()
    {
        var small = new Selection(10, 10);
        var current = small;
        var before = SelectionSnapshot.Capture(small);
        small.SelectAll();
        var memento = SelectionMemento.Create("Select All", before, () => current, () => { })!;

        current = new Selection(20, 20);   // the document was replaced, e.g. by a crop
        current.SelectAll();
        memento.Undo();
        Assert.True(current.IsActive);
    }

    [Fact]
    public void EmptyAreaDoesNotStretchAPixelArea()
    {
        var history = new HistoryStack();
        var selection = new Selection(10, 10);
        var before = SelectionSnapshot.Capture(selection);
        selection.SelectAll();
        history.Push(new CompositeMemento("Move",
            new AreaMemento(),
            SelectionMemento.Create("sel", before, () => selection, () => { })!));
        history.Undo();
        Assert.Equal((5, 5, 2, 2), history.LastChangedArea);
    }

    private sealed class AreaMemento() : HistoryMemento("pixels")
    {
        public override (int X, int Y, int Width, int Height)? ChangedArea => (5, 5, 2, 2);
        public override HistoryMemento Undo() => this;
    }
}
