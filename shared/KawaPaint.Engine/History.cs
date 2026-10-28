// KawaPaint - undo/redo.
//
// The timeline is indexed rather than a pair of stacks, so the History panel can show every step
// and jump straight to one, and it is a tree underneath, so an edit made after undoing starts a
// new branch instead of destroying the undone steps. Steps carry a byte cost, and the stack trims
// or spills to disk once the configured budget is exceeded - which is what makes an unbounded
// step count affordable in the first place.
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

    /// <summary>
    /// The canvas rectangle whose pixels undoing or redoing this step can change, so the editor
    /// recomposites just that. Null means it may change anything - layer structure, several
    /// layers, or simply unknown - and the whole canvas is redrawn.
    /// </summary>
    public virtual (int X, int Y, int Width, int Height)? ChangedArea => null;

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
    private readonly bool _applied;
    private readonly Func<bool, long>? _approximateBytes;
    private readonly Action<bool>? _dispose;

    /// <param name="approximateBytes">
    /// For a structural edit whose undo/redo closures hold a whole detached Layer, Surface or frame
    /// (add/delete/duplicate layer, merge down) rather than just scalars. It is handed whether the
    /// edit is currently applied, because that - not the document's state - says which side owns the
    /// detached object: a delete owns its layer while applied, an add owns its layer while undone.
    /// Omitted for the common case (toggle visibility, blend mode, opacity, reorder) where the
    /// closures only capture scalars.
    /// </param>
    /// <param name="dispose">
    /// Frees whatever <paramref name="approximateBytes"/> is reporting, once this step is dropped
    /// from history for good; same applied flag. Asking the document instead ("do you still own
    /// this layer?") is not enough once history branches: a redo-side "Delete Layer" discarded
    /// with an abandoned branch would see the layer missing - detached by a step of the branch the
    /// user is on now, which still needs it - and free it under that step.
    /// </param>
    public DelegateMemento(string name, Action undo, Action redo,
        Func<bool, long>? approximateBytes = null, Action<bool>? dispose = null)
        : this(name, undo, redo, applied: true, approximateBytes, dispose) { }

    private DelegateMemento(string name, Action undo, Action redo, bool applied,
        Func<bool, long>? approximateBytes, Action<bool>? dispose) : base(name)
    {
        _undo = undo;
        _redo = redo;
        _applied = applied;
        _approximateBytes = approximateBytes;
        _dispose = dispose;
    }

    public override long ApproximateBytes => _approximateBytes?.Invoke(_applied) ?? 0;

    public override HistoryMemento Undo()
    {
        _undo();
        return new DelegateMemento(Name, _redo, _undo, !_applied, _approximateBytes, _dispose);
    }

    public override void Dispose() => _dispose?.Invoke(_applied);
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

    private readonly (int X, int Y, int Width, int Height) _area;

    private TileDeltaMemento(Layer layer, List<Tile> tiles, string name) : base(name)
    {
        _layer = layer;
        _tiles = tiles;
        int left = int.MaxValue, top = int.MaxValue, right = 0, bottom = 0;
        foreach (var tile in tiles)
        {
            left = Math.Min(left, tile.X);
            top = Math.Min(top, tile.Y);
            right = Math.Max(right, tile.X + tile.Width);
            bottom = Math.Max(bottom, tile.Y + tile.Height);
        }
        _area = (left, top, right - left, bottom - top);
    }

    public override (int X, int Y, int Width, int Height)? ChangedArea => _area;

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
/// <param name="Alternatives">
/// Branches leaving the state after this step other than the one the timeline continues into:
/// edits made after undoing back to here, or the steps those edits displaced.
/// </param>
public readonly record struct HistoryStep(int Index, string Name, bool IsApplied, bool IsCurrent, long Bytes,
    int Alternatives = 0);

/// <summary>One branch leaving a state, as offered by <see cref="HistoryStack.BranchesAt"/>.</summary>
/// <param name="Name">The branch's first step.</param>
/// <param name="Length">Steps from its first step to its tip, following the most recently used path.</param>
/// <param name="IsCurrent">The timeline currently continues into this branch.</param>
public readonly record struct HistoryBranch(int Index, string Name, int Length, bool IsCurrent);

/// <summary>
/// The undo timeline, kept as a tree so nothing is lost to an undo.
///
/// Undoing a few steps and then making a new edit used to throw the undone steps away. Here the
/// new edit starts a sibling branch instead, and the old steps stay reachable through
/// <see cref="BranchesAt"/>/<see cref="SwitchBranch"/>. No command log is needed for that: every
/// memento is its own inverse and describes its edit relative to its parent's state, and that
/// state is the same whichever branch the editor reached it from - so moving to another branch is
/// undoing up to the common ancestor and redoing down the other side.
///
/// Everything linear is a view of one path through the tree, the "timeline": from the start,
/// through the applied steps, then on along whichever child of each step was used last.
/// Positions run from 0 (nothing applied) to <see cref="Count"/> (the timeline's tip);
/// <see cref="Position"/> is where the caret sits, and the timeline's entries below it are
/// undoable while the entries at or above it are redoable. Steps on other branches are always in
/// their un-applied (redo) form.
/// </summary>
public sealed class HistoryStack
{
    private sealed class Node
    {
        public HistoryMemento? Memento;   // null only for the root, the empty starting state
        public object? Scope;             // see CaptureScope
        public Node? Parent;
        public readonly List<Node> Children = new();

        /// <summary>The child the timeline continues into; null ends the timeline here.</summary>
        public Node? Next;

        /// <summary>
        /// Clock tick when this node was last on the timeline. An ancestor is on the timeline
        /// whenever a descendant is, so a branch's first node carries the branch's latest use.
        /// </summary>
        public long LastUsed;
    }

    private Node _root = new();
    private readonly List<Node> _line = new();   // the timeline: _line[i] is step index i
    private int _position;
    private int _nodeCount;                      // steps in the whole tree, every branch included
    private long _residentBytes;
    private long _clock;

    /// <summary>
    /// Running total of steps ever removed from the front by <see cref="DropOldest"/>. Every such
    /// removal shifts all surviving indices down by one, so any caller holding an index across an
    /// operation that can trim must rebase it by the delta in this counter (see TruncateFrom).
    /// Monotonic and long-typed so it can never wrap or be confused with a live index.
    /// </summary>
    private long _dropCount;

    /// <summary>Steps nearest the caret are never spilled, since they are the likely next undo.</summary>
    private const int ResidentWindow = 4;

    public event EventHandler? Changed;

    /// <summary>
    /// Records where an edit happened (the editor uses the active animation frame) when it is
    /// pushed. Before a step is undone or redone, <see cref="EnterScope"/> is handed that value
    /// back so the editor can return there first: structural steps act on "the current layer
    /// stack", which is only the right one if it is the stack they were recorded against.
    /// </summary>
    public Func<object?>? CaptureScope { get; set; }

    /// <summary>See <see cref="CaptureScope"/>.</summary>
    public Action<object?>? EnterScope { get; set; }

    /// <summary>
    /// Ceiling on steps across every branch. Zero means unlimited - the memory budget is then the
    /// only bound. Abandoned branches are let go before any step of the timeline is.
    /// </summary>
    public int MaxSteps { get; set; }

    /// <summary>Soft ceiling on resident step data. Zero disables the check entirely.</summary>
    public long MemoryBudgetBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Where spilled payloads are parked. Null disables spilling, so trimming drops steps.</summary>
    public string? SpillDirectory { get; set; }

    /// <summary>
    /// When false, a new edit discards every step leaving from the state it is made in - the
    /// undone steps it replaces, as a plain undo stack does, and any branches kept there earlier.
    /// Branches elsewhere are left alone until trimmed or cleared.
    /// </summary>
    public bool KeepBranches { get; set; } = true;

    /// <summary>Steps on the timeline.</summary>
    public int Count => _line.Count;
    public int Position => _position;

    /// <summary>Steps on every branch, the timeline included.</summary>
    public int TotalCount => _nodeCount;

    public bool CanUndo => _position > 0;
    public bool CanRedo => _position < _line.Count;

    /// <summary>Number of undoable steps. Kept for callers that predate the indexed model.</summary>
    public int UndoCount => _position;

    public string? NextUndoName => CanUndo ? _line[_position - 1].Memento!.Name : null;
    public string? NextRedoName => CanRedo ? _line[_position].Memento!.Name : null;

    public long ResidentBytes => _residentBytes;

    /// <summary>
    /// Union of <see cref="HistoryMemento.ChangedArea"/> over the steps the last Undo, Redo,
    /// JumpTo or SwitchBranch crossed; null when any of them could have changed anything.
    /// </summary>
    public (int X, int Y, int Width, int Height)? LastChangedArea { get; private set; }
    private bool _lastAreaUnknown;

    /// <summary>Snapshot of the timeline for the History panel, oldest first.</summary>
    public IReadOnlyList<HistoryStep> Steps()
    {
        var rows = new List<HistoryStep>(_line.Count);
        for (int i = 0; i < _line.Count; i++)
        {
            var node = _line[i];
            rows.Add(new HistoryStep(i, node.Memento!.Name, i < _position, i == _position - 1,
                node.Memento.ApproximateBytes, Alternatives(node)));
        }
        return rows;
    }

    /// <summary>
    /// Branches leaving the state at caret <paramref name="position"/> (0 is the start) that the
    /// timeline does not continue into.
    /// </summary>
    public int AlternativesAt(int position) =>
        position >= 0 && position <= _line.Count ? Alternatives(NodeAt(position)) : 0;

    /// <summary>
    /// Every branch leaving the state at caret <paramref name="position"/>, oldest first,
    /// including the one the timeline continues into.
    /// </summary>
    public IReadOnlyList<HistoryBranch> BranchesAt(int position)
    {
        if (position < 0 || position > _line.Count) return Array.Empty<HistoryBranch>();
        var node = NodeAt(position);
        var branches = new List<HistoryBranch>(node.Children.Count);
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            int length = 0;
            for (var n = child; n is not null; n = n.Next) length++;
            branches.Add(new HistoryBranch(i, child.Memento!.Name, length, ReferenceEquals(node.Next, child)));
        }
        return branches;
    }

    /// <summary>
    /// Records an edit. Capture the memento BEFORE mutating, then push it. A null memento is
    /// ignored, so a delta that found no changes costs nothing.
    /// </summary>
    public void Push(HistoryMemento? memento)
    {
        if (memento is null) return;

        var parent = Current;
        if (!KeepBranches)
            while (parent.Children.Count > 0) Discard(parent.Children[^1]);
        // Otherwise the steps undone back to here stay where they are, as a sibling branch.

        var node = new Node { Memento = memento, Scope = CaptureScope?.Invoke(), Parent = parent };
        parent.Children.Add(node);
        parent.Next = node;
        _nodeCount++;
        _residentBytes += GetResidentBytes(memento);
        RebuildLine();
        _position = _line.Count;

        Trim();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        StartChangedArea();
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
        StartChangedArea();
        StepForward();
        Trim();   // see Undo()
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves the caret to <paramref name="position"/> on the timeline, applying or reversing
    /// whatever lies between. This is what clicking a row in the History panel does.
    /// </summary>
    public void JumpTo(int position)
    {
        position = Math.Clamp(position, 0, _line.Count);
        if (position == _position) return;

        StartChangedArea();
        MoveCaret(position);
        Trim();   // see Undo() - one pass at the end, not per step, so a long jump stays O(n)
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Makes the timeline continue from caret <paramref name="position"/> into branch
    /// <paramref name="branch"/> of <see cref="BranchesAt"/>, and moves the caret to that
    /// branch's tip - back to the picture as it was when that branch was left.
    /// </summary>
    public void SwitchBranch(int position, int branch)
    {
        if (position < 0 || position > _line.Count) return;
        var fork = NodeAt(position);
        if (branch < 0 || branch >= fork.Children.Count) return;

        StartChangedArea();
        // No Trim() until the end: it can drop from the front, which would renumber `position`
        // under us and could even let go of the branch being switched to.
        MoveCaret(position);
        fork.Next = fork.Children[branch];
        RebuildLine();
        while (_position < _line.Count) StepForward();
        Trim();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes the timeline's step at <paramref name="index"/> and everything after it - every
    /// branch growing out of those steps included - reversing them first if they are applied.
    /// Branches that leave the timeline before <paramref name="index"/> are kept.
    ///
    /// Steps are not independent - a structural edit such as a layer add is meaningless once the
    /// steps around it are gone - so this deliberately truncates rather than plucking a step out
    /// of the middle. Per-item removal would need a replayable command log instead of snapshots.
    /// </summary>
    public void TruncateFrom(int index)
    {
        if (index < 0 || index >= _line.Count) return;
        StartChangedArea();

        if (_position > index)
        {
            // JumpTo ends in Trim(), which can DropOldest() - and that removes from the FRONT,
            // renumbering every surviving step. Reusing the caller's index afterwards would cut at
            // the wrong place: destroying steps the user meant to keep, or silently doing nothing.
            // Not hypothetical: jumping backward un-spills each step it crosses, and a detached-layer
            // DelegateMemento flips its byte report from 0 to full-surface the moment undo detaches
            // the layer, so a backward jump really can push ResidentBytes over budget and into the
            // drop loop. Rebasing by the drop count keeps `index` pointing at the same logical step.
            long droppedBefore = _dropCount;
            JumpTo(index);
            index -= (int)(_dropCount - droppedBefore);

            // Negative means the targeted step was itself one of the dropped ones. Everything still
            // in the list is then "after" it, so truncating from it means truncating from the start.
            if (index < 0) index = 0;
            if (index >= _line.Count)
            {
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        Discard(_line[index]);
        RebuildLine();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        while (_root.Children.Count > 0) Discard(_root.Children[^1]);
        _root = new Node();
        _line.Clear();
        _position = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Node Current => NodeAt(_position);

    private Node NodeAt(int position) => position == 0 ? _root : _line[position - 1];

    private static int Alternatives(Node node) =>
        node.Children.Count - (node.Next is null ? 0 : 1);

    /// <summary>Re-derives the timeline from the root's Next chain and stamps it as used.</summary>
    private void RebuildLine()
    {
        _line.Clear();
        long now = ++_clock;
        _root.LastUsed = now;
        for (var node = _root.Next; node is not null; node = node.Next)
        {
            node.LastUsed = now;
            _line.Add(node);
        }
        if (_position > _line.Count) _position = _line.Count;
    }

    private void MoveCaret(int position)
    {
        while (_position > position) StepBackward();
        while (_position < position) StepForward();
    }

    private void StartChangedArea()
    {
        LastChangedArea = null;
        _lastAreaUnknown = false;
    }

    private void AddChangedArea(HistoryMemento memento)
    {
        if (_lastAreaUnknown) return;
        if (memento.ChangedArea is not { } area)
        {
            _lastAreaUnknown = true;
            LastChangedArea = null;
            return;
        }
        // An empty area (a selection-only step) changes no pixels and must not stretch the union.
        if (area.Width == 0 || area.Height == 0)
        {
            LastChangedArea ??= (0, 0, 0, 0);
            return;
        }
        if (LastChangedArea is { } sofar && sofar.Width > 0 && sofar.Height > 0)
        {
            int left = Math.Min(sofar.X, area.X), top = Math.Min(sofar.Y, area.Y);
            int right = Math.Max(sofar.X + sofar.Width, area.X + area.Width);
            int bottom = Math.Max(sofar.Y + sofar.Height, area.Y + area.Height);
            area = (left, top, right - left, bottom - top);
        }
        LastChangedArea = area;
    }

    private void StepBackward()
    {
        Toggle(_line[_position - 1]);
        _position--;
    }

    private void StepForward()
    {
        Toggle(_line[_position]);
        _position++;
    }

    /// <summary>Applies an un-applied step or reverses an applied one, keeping the byte tally.</summary>
    private void Toggle(Node node)
    {
        var memento = node.Memento!;
        AddChangedArea(memento);
        EnterScope?.Invoke(node.Scope);
        long before = GetResidentBytes(memento);
        (memento as ISpillableMemento)?.Restore();
        var replacement = memento.Undo();
        node.Memento = replacement;
        _residentBytes += GetResidentBytes(replacement) - before;
    }

    /// <summary>
    /// Removes <paramref name="node"/> and every step below it from the tree, newest first. The
    /// timeline is left for the caller to rebuild.
    /// </summary>
    private void Discard(Node node)
    {
        while (node.Children.Count > 0) Discard(node.Children[^1]);

        _residentBytes -= GetResidentBytes(node.Memento!);
        node.Memento!.Dispose();
        _nodeCount--;

        var parent = node.Parent!;
        parent.Children.Remove(node);
        if (ReferenceEquals(parent.Next, node)) parent.Next = null;
    }

    /// <summary>
    /// Enforces the step limit and the memory budget.
    ///
    /// Abandoned branches go first: they are the least likely to be wanted again, and letting one
    /// go never shortens the undo the user is actually working with. Then steps outside the
    /// resident window are spilled to disk, branches first and the timeline oldest-first; a
    /// spilled step costs no memory, so once everything spillable is spilled the resident window
    /// is the floor and history keeps growing on disk instead. Without a spill directory the only
    /// lever left is dropping the oldest steps, which makes them permanently un-undoable.
    /// </summary>
    private void Trim()
    {
        // Unconditional: MaxSteps is documented as a hard ceiling on the step count ("the memory
        // budget is then the only bound" otherwise), not one that only applies while something's
        // undoable. DropOldest is safe to call at _position == 0 too - see its own comment.
        if (MaxSteps > 0)
        {
            while (_nodeCount > MaxSteps && OldestBranch() is { } branch) Discard(branch);
            while (_nodeCount > MaxSteps && _line.Count > 0) DropOldest();
        }

        if (MemoryBudgetBytes <= 0) return;

        long resident = _residentBytes;
        if (resident <= MemoryBudgetBytes) return;

        if (SpillDirectory is not null)
        {
            foreach (var node in SpillOrder())
            {
                if (resident <= MemoryBudgetBytes) break;
                if (node.Memento is not ISpillableMemento { IsSpilled: false } spillable) continue;

                long before = spillable.ResidentBytes;
                try
                {
                    spillable.SpillTo(SpillDirectory);
                    resident -= before;
                    _residentBytes -= before - spillable.ResidentBytes;
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

        // Whatever is still over budget comes out of the abandoned branches next, oldest first -
        // but only branches that actually hold memory. One already spilled whole buys nothing
        // back, and destroying it for nothing would be as wrong as the timeline case below.
        while (resident > MemoryBudgetBytes && OldestBranch(mustHoldMemory: true) is { } heavy)
        {
            long before = _residentBytes;
            Discard(heavy);
            resident -= before - _residentBytes;
        }

        // Never drop the steps nearest the caret: those are the ones about to be undone, and a
        // pathologically small budget should degrade to a short history rather than to none.
        while (resident > MemoryBudgetBytes && _position > ResidentWindow)
        {
            var oldest = _line[0].Memento!;
            long reclaimed = oldest is ISpillableMemento spilled
                ? spilled.ResidentBytes
                : oldest.ApproximateBytes;

            // Everything droppable is already on disk, so dropping buys no memory back: stop and
            // let history keep growing rather than destroying steps for nothing.
            if (reclaimed == 0 && SpillDirectory is not null) break;

            long before = _residentBytes;
            DropOldest();
            resident -= before - _residentBytes;
        }
    }

    /// <summary>Off-timeline branches first, then the timeline oldest-first outside the resident window.</summary>
    private IEnumerable<Node> SpillOrder()
    {
        var onLine = new HashSet<Node>(_line);
        var stack = new Stack<Node>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.Children) stack.Push(child);
            if (node.Memento is not null && !onLine.Contains(node)) yield return node;
        }

        for (int i = 0; i < _line.Count; i++)
            if (Math.Abs(i - _position) > ResidentWindow) yield return _line[i];
    }

    /// <summary>
    /// The first node of the least recently used branch the timeline does not run through, or
    /// null when there is none. Every such branch hangs off the timeline (or the start), since the
    /// timeline's nodes are the only ones whose ancestors are all on it.
    /// </summary>
    private Node? OldestBranch(bool mustHoldMemory = false)
    {
        Node? oldest = null;
        for (int i = 0; i <= _line.Count; i++)
        {
            var fork = NodeAt(i);
            foreach (var child in fork.Children)
            {
                if (ReferenceEquals(child, fork.Next)) continue;
                if (mustHoldMemory && SubtreeResidentBytes(child) == 0) continue;
                if (oldest is null || child.LastUsed < oldest.LastUsed) oldest = child;
            }
        }
        return oldest;
    }

    private static long SubtreeResidentBytes(Node node)
    {
        long total = GetResidentBytes(node.Memento!);
        foreach (var child in node.Children) total += SubtreeResidentBytes(child);
        return total;
    }

    /// <summary>
    /// Drops the oldest step of the timeline, whether it's currently undoable (_position > 0, the
    /// common case) or only redoable (_position == 0: nothing is applied, but MaxSteps still has to
    /// be enforceable even then). Every memento type's stored content is a self-contained absolute
    /// snapshot for its own tile/surface/document region - never a diff relative to a neighboring
    /// step - so dropping the front index is safe from either side: it just makes that one edit
    /// permanently unreachable, the same "bounded history" trade-off either way, not a correctness
    /// hazard. Its children move up to the start.
    ///
    /// When the dropped step was applied, the start state it was undoable back to is gone for good,
    /// and with it every other branch that left from there - those go too, since nothing could
    /// ever reach them again.
    /// </summary>
    private void DropOldest()
    {
        var oldest = _line[0];
        if (_position > 0)
            for (int i = _root.Children.Count - 1; i >= 0; i--)
                if (!ReferenceEquals(_root.Children[i], oldest)) Discard(_root.Children[i]);

        _residentBytes -= GetResidentBytes(oldest.Memento!);
        oldest.Memento!.Dispose();
        _nodeCount--;

        int at = _root.Children.IndexOf(oldest);
        _root.Children.RemoveAt(at);
        _root.Children.InsertRange(at, oldest.Children);
        foreach (var child in oldest.Children) child.Parent = _root;
        _root.Next = oldest.Next;

        _dropCount++;   // lets TruncateFrom rebase a caller-supplied index across this renumbering
        _line.RemoveAt(0);
        // Only undoable steps (index < position) sit before the caret, so only removing one of
        // those needs the caret's absolute index shifted down to keep pointing at the same logical
        // spot. Removing a redoable step (_position == 0, so index 0 is never < position) leaves
        // the caret's meaning unchanged - decrementing it here would send it negative.
        if (_position > 0) _position--;
    }

    private static long GetResidentBytes(HistoryMemento step) =>
        step is ISpillableMemento spillable ? spillable.ResidentBytes : step.ApproximateBytes;
}
