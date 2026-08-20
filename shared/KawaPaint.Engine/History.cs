// KawaPaint — undo/redo.
//
// The stack is an indexed list rather than a pair of stacks, so the History panel can show every
// step and jump straight to one. Steps carry a byte cost, and the stack trims or spills to disk
// once the configured budget is exceeded — which is what makes an unbounded step count
// affordable in the first place.
//
// A memento is its own inverse: Undo() applies the reversal and returns the memento that
// re-applies the edit, so redo is just "undo the undo" and one list position holds both.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace KawaPaint.Engine;

/// <summary>A reversible edit.</summary>
public abstract class HistoryMemento : IDisposable
{
    public string Name { get; }
    protected HistoryMemento(string name) => Name = name;

    /// <summary>
    /// Roughly how much memory this step holds, used for the history budget. Structural steps
    /// that only capture a couple of references report zero.
    /// </summary>
    public virtual long ApproximateBytes => 0;

    public abstract HistoryMemento Undo();
    public virtual void Dispose() { }
}

/// <summary>Implemented by mementos whose payload can be parked on disk and read back on demand.</summary>
public interface ISpillableMemento
{
    /// <summary>Bytes currently held in memory; zero once spilled.</summary>
    long ResidentBytes { get; }

    bool IsSpilled { get; }

    /// <summary>Writes the payload under <paramref name="directory"/> and frees it from memory.</summary>
    void SpillTo(string directory);

    /// <summary>Reads the payload back into memory. A no-op when it was never spilled.</summary>
    void Restore();
}

/// <summary>
/// A memento defined by an undo action and a redo action. Undo() runs the undo action and
/// returns its mirror, so it works in both directions. Good for discrete structural edits
/// (add/delete/reorder/toggle a layer).
/// </summary>
public sealed class DelegateMemento : HistoryMemento
{
    private readonly Action _undo;
    private readonly Action _redo;
    private readonly Func<long>? _approximateBytes;
    private readonly Action? _dispose;

    /// <param name="approximateBytes">
    /// For a structural edit whose undo/redo closures hold a whole detached Layer or Surface (add
    /// /delete/duplicate layer, merge down) rather than just scalars — a live query, not a captured
    /// value, since which side (undo or redo) currently owns the detached object flips with every
    /// toggle. Typically written as "0 while the document owns it, real bytes while only this
    /// memento does" (e.g. via <c>Document.IndexOf</c>). Omitted for the common case (toggle
    /// visibility, blend mode, opacity, reorder) where the closures only capture scalars.
    /// </param>
    /// <param name="dispose">
    /// Frees whatever <paramref name="approximateBytes"/> is reporting, once this step is dropped
    /// from history for good. Must apply the same "only if the document doesn't currently own it"
    /// check <paramref name="approximateBytes"/> does — a step can be discarded while its detached
    /// object is actually the live, undone side (e.g. a delete's redo branch invalidated right after
    /// an undo reattached the layer), and disposing a Surface the Document still owns is a
    /// use-after-dispose waiting to happen.
    /// </param>
    public DelegateMemento(string name, Action undo, Action redo,
        Func<long>? approximateBytes = null, Action? dispose = null) : base(name)
    {
        _undo = undo;
        _redo = redo;
        _approximateBytes = approximateBytes;
        _dispose = dispose;
    }

    public override long ApproximateBytes => _approximateBytes?.Invoke() ?? 0;

    public override HistoryMemento Undo()
    {
        _undo();
        return new DelegateMemento(Name, _redo, _undo, _approximateBytes, _dispose);
    }

    public override void Dispose() => _dispose?.Invoke();
}

/// <summary>
/// Stores only the tiles of a layer that an edit actually changed. A three-pixel pencil stroke on
/// a large canvas retains a few kilobytes instead of a full-surface clone, which is the
/// difference between a bounded history and an unbounded one.
/// </summary>
public sealed class TileDeltaMemento : HistoryMemento, ISpillableMemento
{
    public const int TileSize = 64;

    private sealed class Tile
    {
        public int X, Y, Width, Height;
        public byte[]? Pixels;   // null once spilled
    }

    private readonly Layer _layer;
    private readonly List<Tile> _tiles;
    private string? _spillPath;

    private TileDeltaMemento(Layer layer, List<Tile> tiles, string name) : base(name)
    {
        _layer = layer;
        _tiles = tiles;
    }

    public int TileCount => _tiles.Count;

    public override long ApproximateBytes
    {
        get
        {
            long total = 0;
            foreach (var tile in _tiles) total += tile.Pixels?.Length ?? 0;
            return total;
        }
    }

    public long ResidentBytes => IsSpilled ? 0 : ApproximateBytes;
    public bool IsSpilled => _spillPath is not null;

    /// <summary>
    /// Captures the tiles where <paramref name="before"/> and the layer's current surface differ.
    /// Returns null when the edit changed nothing, so the caller can skip pushing a no-op step.
    /// Ownership of <paramref name="before"/> stays with the caller.
    /// </summary>
    public static unsafe TileDeltaMemento? Create(Layer layer, Surface before, string name)
    {
        Surface after = layer.Surface;
        if (before.Width != after.Width || before.Height != after.Height)
            throw new ArgumentException("Snapshot size does not match the layer.", nameof(before));

        var tiles = new List<Tile>();

        for (int ty = 0; ty < after.Height; ty += TileSize)
        {
            int height = Math.Min(TileSize, after.Height - ty);
            for (int tx = 0; tx < after.Width; tx += TileSize)
            {
                int width = Math.Min(TileSize, after.Width - tx);
                if (!TileDiffers(before, after, tx, ty, width, height)) continue;

                tiles.Add(new Tile
                {
                    X = tx,
                    Y = ty,
                    Width = width,
                    Height = height,
                    Pixels = ReadTile(before, tx, ty, width, height)
                });
            }
        }

        return tiles.Count == 0 ? null : new TileDeltaMemento(layer, tiles, name);
    }

    /// <summary>
    /// As <see cref="Create"/>, but takes ownership of the snapshot and disposes it. Use at the
    /// end of a live-preview edit, where the snapshot exists only to be diffed against.
    /// </summary>
    public static TileDeltaMemento? Consume(Layer layer, Surface preEditSnapshot, string name)
    {
        try { return Create(layer, preEditSnapshot, name); }
        finally { preEditSnapshot.Dispose(); }
    }

    private static unsafe bool TileDiffers(Surface a, Surface b, int x, int y, int width, int height)
    {
        int rowBytes = width * sizeof(ColorBgra);
        for (int row = 0; row < height; row++)
        {
            var spanA = new ReadOnlySpan<byte>((byte*)a.GetPointPointer(x, y + row), rowBytes);
            var spanB = new ReadOnlySpan<byte>((byte*)b.GetPointPointer(x, y + row), rowBytes);
            if (!spanA.SequenceEqual(spanB)) return true;
        }
        return false;
    }

    private static unsafe byte[] ReadTile(Surface source, int x, int y, int width, int height)
    {
        int rowBytes = width * sizeof(ColorBgra);
        var buffer = new byte[rowBytes * height];
        fixed (byte* dst = buffer)
        {
            for (int row = 0; row < height; row++)
            {
                var src = new ReadOnlySpan<byte>((byte*)source.GetPointPointer(x, y + row), rowBytes);
                src.CopyTo(new Span<byte>(dst + row * rowBytes, rowBytes));
            }
        }
        return buffer;
    }

    private static unsafe void WriteTile(Surface target, Tile tile)
    {
        int rowBytes = tile.Width * sizeof(ColorBgra);
        fixed (byte* src = tile.Pixels!)
        {
            for (int row = 0; row < tile.Height; row++)
            {
                var source = new ReadOnlySpan<byte>(src + row * rowBytes, rowBytes);
                source.CopyTo(new Span<byte>((byte*)target.GetPointPointer(tile.X, tile.Y + row), rowBytes));
            }
        }
    }

    public override HistoryMemento Undo()
    {
        Restore();

        // The inverse covers exactly the same tiles, so capturing the current content of those
        // rects before overwriting them is all redo needs.
        var inverse = new List<Tile>(_tiles.Count);
        foreach (var tile in _tiles)
        {
            inverse.Add(new Tile
            {
                X = tile.X,
                Y = tile.Y,
                Width = tile.Width,
                Height = tile.Height,
                Pixels = ReadTile(_layer.Surface, tile.X, tile.Y, tile.Width, tile.Height)
            });
            WriteTile(_layer.Surface, tile);
        }

        return new TileDeltaMemento(_layer, inverse, Name);
    }

    public void SpillTo(string directory)
    {
        if (IsSpilled || _tiles.Count == 0) return;

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".hst");

        using (var file = File.Create(path))
        using (var deflate = new DeflateStream(file, CompressionLevel.Fastest))
        using (var writer = new BinaryWriter(deflate))
        {
            writer.Write(_tiles.Count);
            foreach (var tile in _tiles)
            {
                writer.Write(tile.X);
                writer.Write(tile.Y);
                writer.Write(tile.Width);
                writer.Write(tile.Height);
                writer.Write(tile.Pixels!.Length);
                writer.Write(tile.Pixels!);
            }
        }

        foreach (var tile in _tiles) tile.Pixels = null;
        _spillPath = path;
    }

    public void Restore()
    {
        if (_spillPath is null) return;

        using (var file = File.OpenRead(_spillPath))
        using (var inflate = new DeflateStream(file, CompressionMode.Decompress))
        using (var reader = new BinaryReader(inflate))
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                reader.ReadInt32();   // x, y, width and height are already known from _tiles
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt32();
                _tiles[i].Pixels = reader.ReadBytes(reader.ReadInt32());
            }
        }

        TryDeleteSpill();
    }

    public override void Dispose()
    {
        TryDeleteSpill();
        _tiles.Clear();
    }

    private void TryDeleteSpill()
    {
        if (_spillPath is null) return;
        try { File.Delete(_spillPath); } catch { /* the recovery directory is cleaned on exit anyway */ }
        _spillPath = null;
    }
}

/// <summary>
/// Snapshots a layer's entire surface. Kept for edits that replace the whole layer, where a
/// delta would capture every tile anyway; prefer <see cref="TileDeltaMemento"/> otherwise.
/// </summary>
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

    public override long ApproximateBytes => _saved is null ? 0 : (long)_saved.Stride * _saved.Height;

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

/// <summary>
/// Reverses a whole-document replacement (crop / resize / rotate / flatten), where the edit
/// produced a new Document rather than mutating layers in place. The document that is currently
/// out of the editor is owned by the memento and disposed with it, so dropping history frees it.
/// </summary>
public sealed class DocumentSwapMemento : HistoryMemento
{
    private readonly Func<Document, Document?> _swap;   // installs a document, returns the displaced one
    private Document? _detached;

    public DocumentSwapMemento(string name, Document detached, Func<Document, Document?> swap) : base(name)
    {
        _detached = detached;
        _swap = swap;
    }

    public override long ApproximateBytes
    {
        get
        {
            if (_detached is null) return 0;
            long total = 0;
            foreach (var layer in _detached.Layers) total += (long)layer.Surface.Stride * layer.Surface.Height;
            return total;
        }
    }

    public override HistoryMemento Undo()
    {
        var incoming = _detached ?? throw new ObjectDisposedException(nameof(DocumentSwapMemento));
        _detached = null;
        var displaced = _swap(incoming)
            ?? throw new InvalidOperationException("document swap produced no displaced document");
        return new DocumentSwapMemento(Name, displaced, _swap);
    }

    public override void Dispose()
    {
        _detached?.Dispose();
        _detached = null;
    }
}

/// <summary>One row in the History panel.</summary>
public readonly record struct HistoryStep(int Index, string Name, bool IsApplied, bool IsCurrent, long Bytes);

/// <summary>
/// The undo timeline. Positions run from 0 (nothing applied) to <see cref="Count"/> (everything
/// applied); <see cref="Position"/> is where the caret sits, and the entries below it are undoable
/// while the entries at or above it are redoable.
/// </summary>
public sealed class HistoryStack
{
    private readonly List<HistoryMemento> _steps = new();
    private int _position;

    /// <summary>Steps nearest the caret are never spilled, since they are the likely next undo.</summary>
    private const int ResidentWindow = 4;

    public event EventHandler? Changed;

    /// <summary>Zero means unlimited — the memory budget is then the only bound.</summary>
    public int MaxSteps { get; set; }

    /// <summary>Soft ceiling on resident step data. Zero disables the check entirely.</summary>
    public long MemoryBudgetBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Where spilled payloads are parked. Null disables spilling, so trimming drops steps.</summary>
    public string? SpillDirectory { get; set; }

    public int Count => _steps.Count;
    public int Position => _position;

    public bool CanUndo => _position > 0;
    public bool CanRedo => _position < _steps.Count;

    /// <summary>Number of undoable steps. Kept for callers that predate the indexed model.</summary>
    public int UndoCount => _position;

    public string? NextUndoName => CanUndo ? _steps[_position - 1].Name : null;
    public string? NextRedoName => CanRedo ? _steps[_position].Name : null;

    public long ResidentBytes
    {
        get
        {
            long total = 0;
            foreach (var step in _steps)
                total += step is ISpillableMemento spillable ? spillable.ResidentBytes : step.ApproximateBytes;
            return total;
        }
    }

    /// <summary>Snapshot of the timeline for the History panel, oldest first.</summary>
    public IReadOnlyList<HistoryStep> Steps()
    {
        var rows = new List<HistoryStep>(_steps.Count);
        for (int i = 0; i < _steps.Count; i++)
            rows.Add(new HistoryStep(i, _steps[i].Name, i < _position, i == _position - 1, _steps[i].ApproximateBytes));
        return rows;
    }

    /// <summary>
    /// Records an edit. Capture the memento BEFORE mutating, then push it. A null memento is
    /// ignored, so a delta that found no changes costs nothing.
    /// </summary>
    public void Push(HistoryMemento? memento)
    {
        if (memento is null) return;

        DiscardFrom(_position);   // a new edit invalidates the redo branch
        _steps.Add(memento);
        _position = _steps.Count;

        Trim();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        StepBackward();
        // Undo un-spills whatever step it crosses (see StepBackward), which can pull an
        // arbitrarily long spilled history back into memory one step at a time with nothing ever
        // re-spilling it. Re-running Trim() re-evaluates the resident window around the caret's
        // new position, so anything that just fell outside it gets spilled straight back.
        Trim();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        StepForward();
        Trim();   // see Undo()
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves the caret to <paramref name="position"/>, applying or reversing whatever lies
    /// between. This is what clicking a row in the History panel does.
    /// </summary>
    public void JumpTo(int position)
    {
        position = Math.Clamp(position, 0, _steps.Count);
        if (position == _position) return;

        while (_position > position) StepBackward();
        while (_position < position) StepForward();
        Trim();   // see Undo() — one pass at the end, not per step, so a long jump stays O(n)
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes the step at <paramref name="index"/> and everything after it, reversing them first
    /// if they are currently applied.
    ///
    /// Steps are not independent — a structural edit such as a layer add is meaningless once the
    /// steps around it are gone — so this deliberately truncates rather than plucking a step out
    /// of the middle. Per-item removal would need a replayable command log instead of snapshots.
    /// </summary>
    public void TruncateFrom(int index)
    {
        if (index < 0 || index >= _steps.Count) return;

        if (_position > index) JumpTo(index);
        DiscardFrom(index);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        DiscardFrom(0);
        _position = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StepBackward()
    {
        var memento = _steps[_position - 1];
        (memento as ISpillableMemento)?.Restore();
        _steps[_position - 1] = memento.Undo();
        _position--;
    }

    private void StepForward()
    {
        var memento = _steps[_position];
        (memento as ISpillableMemento)?.Restore();
        _steps[_position] = memento.Undo();
        _position++;
    }

    private void DiscardFrom(int index)
    {
        for (int i = _steps.Count - 1; i >= index; i--)
        {
            _steps[i].Dispose();
            _steps.RemoveAt(i);
        }
        if (_position > _steps.Count) _position = _steps.Count;
    }

    /// <summary>
    /// Enforces the step limit and the memory budget.
    ///
    /// Steps outside the resident window are spilled to disk oldest-first; a spilled step costs
    /// no memory, so once everything spillable is spilled the resident window is the floor and
    /// history keeps growing on disk instead. Without a spill directory the only lever is
    /// dropping the oldest steps, which makes them permanently un-undoable.
    /// </summary>
    private void Trim()
    {
        if (MaxSteps > 0)
            while (_steps.Count > MaxSteps && _position > 0) DropOldest();

        if (MemoryBudgetBytes <= 0) return;

        // Tracked as a running total: re-reading ResidentBytes inside the loops below would make
        // trimming quadratic in the number of steps.
        long resident = ResidentBytes;
        if (resident <= MemoryBudgetBytes) return;

        if (SpillDirectory is not null)
        {
            for (int i = 0; i < _steps.Count && resident > MemoryBudgetBytes; i++)
            {
                if (Math.Abs(i - _position) <= ResidentWindow) continue;
                if (_steps[i] is not ISpillableMemento { IsSpilled: false } spillable) continue;

                long before = spillable.ResidentBytes;
                try
                {
                    spillable.SpillTo(SpillDirectory);
                    resident -= before;
                }
                catch
                {
                    // Out of disk, or an unwritable directory: stop spilling and fall through to
                    // dropping steps, which always works.
                    SpillDirectory = null;
                    break;
                }
            }
        }

        // Never drop the steps nearest the caret: those are the ones about to be undone, and a
        // pathologically small budget should degrade to a short history rather than to none.
        while (resident > MemoryBudgetBytes && _position > ResidentWindow)
        {
            long reclaimed = _steps[0] is ISpillableMemento spilled
                ? spilled.ResidentBytes
                : _steps[0].ApproximateBytes;

            // Everything droppable is already on disk, so dropping buys no memory back: stop and
            // let history keep growing rather than destroying steps for nothing.
            if (reclaimed == 0 && SpillDirectory is not null) break;

            DropOldest();
            resident -= reclaimed;
        }
    }

    /// <summary>Drops the oldest undoable step. It becomes permanently un-undoable, as with any bounded history.</summary>
    private void DropOldest()
    {
        _steps[0].Dispose();
        _steps.RemoveAt(0);
        _position--;
    }
}
