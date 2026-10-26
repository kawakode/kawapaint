using KawaPaint.Engine;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>Frame ownership rules the undo history relies on (issues #3, #6, #13).</summary>
public class DocumentFrameTests
{
    [Fact]
    public void DetachedFrameStaysAliveAndCanBeReinserted()
    {
        using var document = new Document(8, 8);
        document.AddLayer();
        DocumentFrame second = document.AddFrame();
        Layer layer = second.Layers[0];

        DocumentFrame detached = document.DetachFrameAt(1);

        Assert.Same(second, detached);
        Assert.Equal(1, document.FrameCount);
        Assert.Equal(0, document.ActiveFrameIndex);
        Assert.True(document.OwnsLayer(layer));
        _ = layer.Surface[0, 0];   // throws once disposed

        document.InsertFrame(1, detached);
        Assert.Equal(2, document.FrameCount);
        Assert.Equal(1, document.ActiveFrameIndex);
        Assert.Same(layer, document.Layers[0]);
    }

    [Fact]
    public void ReleasingADetachedFrameDisposesIt()
    {
        using var document = new Document(8, 8);
        document.AddLayer();
        Layer layer = document.AddFrame().Layers[0];
        DocumentFrame detached = document.DetachFrameAt(1);

        document.ReleaseDetachedFrame(detached);

        Assert.False(document.OwnsLayer(layer));
        Assert.Throws<ObjectDisposedException>(() => layer.Surface[0, 0]);
    }

    [Fact]
    public void ReleasingAnAttachedFrameDoesNothing()
    {
        using var document = new Document(8, 8);
        document.AddLayer();
        DocumentFrame frame = document.AddFrame();

        document.ReleaseDetachedFrame(frame);

        _ = frame.Layers[0].Surface[0, 0];
    }

    [Fact]
    public void OwnsLayerLooksBeyondTheActiveFrame()
    {
        using var document = new Document(8, 8);
        Layer first = document.AddLayer();
        document.AddFrame();

        Assert.Equal(-1, document.IndexOf(first));
        Assert.True(document.OwnsLayer(first));
        Assert.False(document.OwnsLayer(new Layer(8, 8)));
    }

    [Fact]
    public void InsertingBeforeTheActiveFrameKeepsItActive()
    {
        using var document = new Document(8, 8);
        document.AddLayer();
        DocumentFrame second = document.AddFrame();
        DocumentFrame third = document.AddFrame();
        DocumentFrame detached = document.DetachFrameAt(1);   // active stays on "third"

        document.InsertFrame(0, detached, makeActive: false);

        Assert.Same(third, document.ActiveFrame);
        Assert.Same(second, document.Frames[0]);
    }

    [Fact]
    public void HistoryReturnsToTheRecordedScopeBeforeEachStep()
    {
        object? current = "a";
        var entered = new List<object?>();
        var history = new HistoryStack
        {
            CaptureScope = () => current,
            EnterScope = scope => { entered.Add(scope); current = scope; }
        };
        history.Push(new DelegateMemento("one", () => { }, () => { }));
        current = "b";
        history.Push(new DelegateMemento("two", () => { }, () => { }));
        current = "c";

        history.Undo();
        history.Undo();
        history.Redo();

        Assert.Equal(new object?[] { "b", "a", "a" }, entered);
    }
}
