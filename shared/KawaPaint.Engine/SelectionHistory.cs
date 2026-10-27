// KawaPaint - undo steps for selection changes. A mask is mostly long runs of 0 or 255, so a
// snapshot is stored run-length encoded: a rectangle on a 4000x3000 canvas costs a few KB rather
// than 12 MB, which lets every selection change be its own step without eating the history budget.

namespace KawaPaint.Engine;

/// <summary>An exact, compressed copy of a selection mask and its active flag.</summary>
public sealed class SelectionSnapshot
{
    private readonly byte[] _runs;

    public int Width { get; }
    public int Height { get; }
    public bool IsActive { get; }
    public long ApproximateBytes => _runs.Length;

    private SelectionSnapshot(int width, int height, bool active, byte[] runs)
    {
        Width = width;
        Height = height;
        IsActive = active;
        _runs = runs;
    }

    public static SelectionSnapshot Capture(Selection selection)
    {
        ReadOnlySpan<byte> mask = selection.Mask;
        var runs = new List<byte>();
        // An inactive selection keeps an all-zero mask, so it encodes to a single run anyway.
        int i = 0;
        while (i < mask.Length)
        {
            byte value = mask[i];
            int length = mask[i..].IndexOfAnyExcept(value);
            if (length < 0) length = mask.Length - i;
            runs.Add(value);
            for (uint n = (uint)length; ; n >>= 7)
            {
                if (n < 0x80) { runs.Add((byte)n); break; }
                runs.Add((byte)(n | 0x80));
            }
            i += length;
        }
        return new SelectionSnapshot(selection.Width, selection.Height, selection.IsActive, runs.ToArray());
    }

    /// <summary>Whether <paramref name="selection"/> currently holds exactly this state.</summary>
    public bool Matches(Selection selection) =>
        selection.Width == Width && selection.Height == Height && selection.IsActive == IsActive
        && Capture(selection)._runs.AsSpan().SequenceEqual(_runs);

    /// <summary>Puts this state into <paramref name="selection"/>; false when the sizes differ.</summary>
    public bool RestoreTo(Selection selection)
    {
        if (selection.Width != Width || selection.Height != Height) return false;
        var mask = new byte[Width * Height];
        int position = 0, i = 0;
        while (i < _runs.Length)
        {
            byte value = _runs[i++];
            int length = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = _runs[i++];
                length |= (b & 0x7F) << shift;
                if (b < 0x80) break;
            }
            mask.AsSpan(position, length).Fill(value);
            position += length;
        }
        selection.Restore(mask, IsActive);
        return true;
    }
}

/// <summary>
/// One selection change. The selection object is looked up when the step runs rather than held,
/// because replacing the document (crop, resize, rotate) gives the canvas a new one; a step whose
/// size no longer matches the current canvas does nothing.
/// </summary>
public sealed class SelectionMemento : HistoryMemento
{
    private readonly Func<Selection?> _current;
    private readonly Action _changed;
    private readonly SelectionSnapshot _state;

    /// <param name="before">The selection as it was before the change; undo restores it.</param>
    public SelectionMemento(string name, SelectionSnapshot before, Func<Selection?> current, Action changed)
        : base(name)
    {
        _state = before;
        _current = current;
        _changed = changed;
    }

    /// <summary>A step for the change from <paramref name="before"/> to the current selection,
    /// or null when nothing changed.</summary>
    public static SelectionMemento? Create(string name, SelectionSnapshot before, Func<Selection?> current, Action changed)
        => current() is { } selection && !before.Matches(selection)
            ? new SelectionMemento(name, before, current, changed)
            : null;

    public override long ApproximateBytes => _state.ApproximateBytes;

    // Only the ants change, which the canvas draws as an overlay.
    public override (int X, int Y, int Width, int Height)? ChangedArea => (0, 0, 0, 0);

    public override HistoryMemento Undo()
    {
        Selection? selection = _current();
        if (selection is null) return this;
        var now = SelectionSnapshot.Capture(selection);
        if (!_state.RestoreTo(selection)) return this;
        _changed();
        return new SelectionMemento(Name, now, _current, _changed);
    }
}

/// <summary>Several mementos recorded by one action, undone together in reverse order.</summary>
public sealed class CompositeMemento : HistoryMemento
{
    private readonly HistoryMemento[] _parts;

    public CompositeMemento(string name, params HistoryMemento[] parts) : base(name) => _parts = parts;

    /// <summary>The non-null parts as one step: null for none, the part itself for one.</summary>
    public static HistoryMemento? Combine(string name, params HistoryMemento?[] parts)
    {
        HistoryMemento[] present = parts.OfType<HistoryMemento>().ToArray();
        return present.Length switch { 0 => null, 1 => present[0], _ => new CompositeMemento(name, present) };
    }

    public override long ApproximateBytes => _parts.Sum(part => part.ApproximateBytes);

    public override (int X, int Y, int Width, int Height)? ChangedArea
    {
        get
        {
            (int X, int Y, int Width, int Height)? area = null;
            foreach (HistoryMemento part in _parts)
            {
                if (part.ChangedArea is not { } a) return null;
                if (a.Width == 0 || a.Height == 0) continue;
                area = area is not { } b ? a : Union(a, b);
            }
            return area ?? (0, 0, 0, 0);
        }
    }

    public override HistoryMemento Undo()
    {
        var redo = new HistoryMemento[_parts.Length];
        for (int i = _parts.Length - 1; i >= 0; i--) redo[_parts.Length - 1 - i] = _parts[i].Undo();
        return new CompositeMemento(Name, redo);
    }

    public override void Dispose()
    {
        foreach (HistoryMemento part in _parts) part.Dispose();
    }

    private static (int, int, int, int) Union((int X, int Y, int Width, int Height) a, (int X, int Y, int Width, int Height) b)
    {
        int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        int x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return (x0, y0, x1 - x0, y1 - y0);
    }
}
