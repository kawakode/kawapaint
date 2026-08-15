namespace KawaPaint.Engine;

/// <summary>
/// A reversible edit. <see cref="Undo"/> applies the reversal and returns the memento that
/// re-applies the edit (its own inverse), so redo is just "undo the undo".
/// </summary>
public abstract class HistoryMemento : IDisposable
{
    public string Name { get; }
    protected HistoryMemento(string name) => Name = name;

    public abstract HistoryMemento Undo();
    public virtual void Dispose() { }
}

/// <summary>Snapshots a layer's entire surface. Simple and correct; dirty-rects can come later.</summary>
public sealed class LayerSurfaceMemento : HistoryMemento
{
    private readonly Layer _layer;
    private Surface? _saved;

    public LayerSurfaceMemento(Layer layer, string name = "Edit") : base(name)
    {
        _layer = layer;
        _saved = layer.Surface.Clone();
    }

    private LayerSurfaceMemento(Layer layer, Surface saved, string name) : base(name)
    {
        _layer = layer;
        _saved = saved;
    }

    /// <summary>
    /// Builds a memento from an already-captured pre-edit snapshot (ownership transfers to the
    /// memento). Use when the edit was previewed live and the "before" state was saved earlier.
    /// </summary>
    public static LayerSurfaceMemento FromSnapshot(Layer layer, Surface preEditSnapshot, string name)
        => new(layer, preEditSnapshot, name);

    public override HistoryMemento Undo()
    {
        if (_saved is null) throw new ObjectDisposedException(nameof(LayerSurfaceMemento));
        var redo = new LayerSurfaceMemento(_layer, _layer.Surface.Clone(), Name);
        _layer.Surface.CopyFrom(_saved);
        _saved.Dispose();
        _saved = null;
        return redo;
    }

    public override void Dispose()
    {
        _saved?.Dispose();
        _saved = null;
    }
}

/// <summary>Undo/redo stacks over HistoryMementos.</summary>
public sealed class HistoryStack
{
    private readonly Stack<HistoryMemento> _undo = new();
    private readonly Stack<HistoryMemento> _redo = new();

    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;

    /// <summary>Records an edit. Capture the memento BEFORE mutating, then push it.</summary>
    public void Push(HistoryMemento memento)
    {
        _undo.Push(memento);
        ClearRedo();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var m = _undo.Pop();
        _redo.Push(m.Undo());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var m = _redo.Pop();
        _undo.Push(m.Undo());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        while (_undo.Count > 0) _undo.Pop().Dispose();
        ClearRedo();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ClearRedo()
    {
        while (_redo.Count > 0) _redo.Pop().Dispose();
    }
}
