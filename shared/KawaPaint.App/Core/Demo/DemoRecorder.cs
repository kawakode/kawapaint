// KawaPaint - collects the input stream that DemoPlayer later replays.
//
// The recorder is deliberately passive: it owns no UI and reaches into nothing. MainView and
// SurfaceView push notifications at it, and it decides what is worth keeping. Two rules keep the
// stream honest:
//
//   * Suppression. An action that fires through CommandRegistry also runs the very same handler a
//     menu click would, so both paths would otherwise record the same action twice. The registry
//     tap opens a Suppress() scope for the duration of the command, and nested notes are dropped.
//     The same scope is what keeps playback from re-recording itself.
//   * Redundancy. A tool switch to the tool already selected, or a size set to the size already in
//     force, changes nothing a replay would do, so neither is written. Note that this reasoning
//     does NOT extend to repeated pointer positions, which look equally redundant and are not -
//     see NoteStrokeMove.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace KawaPaint.App.Core.Demo;

public sealed class DemoRecorder
{
    private readonly Stopwatch _clock = new();
    private DemoFile? _demo;

    private int _suppress;
    private bool _strokeInFlight;

    // Last values written, for redundancy filtering.
    private string? _lastTool;
    private readonly int?[] _lastParam = new int?[16];
    private uint?[] _lastColor = new uint?[2];
    private int _lastViewMs = int.MinValue;

    /// <summary>Zoom/pan is cosmetic, so it is sampled rather than followed exactly.</summary>
    private const int ViewIntervalMs = 120;

    public bool IsRecording => _demo is not null;

    public int EventCount => _demo?.Events.Count ?? 0;

    public int ElapsedMs => _demo is null ? 0 : (int)_clock.ElapsedMilliseconds;

    /// <summary>Raised after every recorded event, so the UI can show a live counter.</summary>
    public event Action? Progress;

    /// <summary>
    /// Begins a recording. <paramref name="startDocument"/> is the .kwp bytes of the document as
    /// it stands right now; pass null only for a canvas that is genuinely blank, in which case
    /// <paramref name="blankFill"/> reproduces it for free.
    /// </summary>
    public void Start(int canvasWidth, int canvasHeight, byte[]? startDocument, uint blankFill, string appVersion)
    {
        _demo = new DemoFile
        {
            CanvasWidth = canvasWidth,
            CanvasHeight = canvasHeight,
            StartDocument = startDocument,
            BlankFill = blankFill,
            AppVersion = appVersion,
            RecordedUtc = DateTime.UtcNow
        };

        _suppress = 0;
        _strokeInFlight = false;
        _lastTool = null;
        Array.Clear(_lastParam);
        _lastColor = new uint?[2];
        _lastViewMs = int.MinValue;

        _clock.Restart();
    }

    /// <summary>Ends the recording and hands back what was captured. Null if nothing was running.</summary>
    public DemoFile? Stop()
    {
        if (_demo is null) return null;

        // A recording stopped mid-drag would replay as a stroke that never lifts.
        if (_strokeInFlight) NoteStrokeEnd();

        _clock.Stop();
        var done = _demo;
        _demo = null;
        return done;
    }

    /// <summary>
    /// Opens a scope in which notes are ignored. Wrap anything that dispatches through a second
    /// recorded path (a registry command running its menu handler) or anything driven by playback.
    /// </summary>
    public IDisposable Suppress() => new Scope(this);

    private sealed class Scope : IDisposable
    {
        private readonly DemoRecorder _owner;
        private bool _done;
        public Scope(DemoRecorder owner) { _owner = owner; owner._suppress++; }
        public void Dispose() { if (_done) return; _done = true; _owner._suppress--; }
    }

    private bool Off => _demo is null || _suppress > 0;

    private void Add(DemoEvent e)
    {
        _demo!.Events.Add(e);
        Progress?.Invoke();
    }

    private int Now => (int)_clock.ElapsedMilliseconds;

    // ---- pointer ---------------------------------------------------------

    public void NoteStrokeBegin(double x, double y, bool ctrl)
    {
        if (Off) return;
        _strokeInFlight = true;
        Add(DemoEvent.Down(Now, x, y, ctrl));
    }

    /// <summary>
    /// Every pointer sample is kept, including one that repeats the previous position. Dropping
    /// those looks like free compression and is not: the pencil alpha-blends a disc per move, so a
    /// repeated sample stamps a second dab over the first and darkens it. Filtering them made a
    /// replay come out visibly lighter at stroke starts, where the duplicate samples cluster -
    /// caught by diffing a recorded session against its own replay, not by reading the code.
    /// </summary>
    public void NoteStrokeMove(double x, double y)
    {
        if (Off || !_strokeInFlight) return;
        Add(DemoEvent.Move(Now, x, y));
    }

    public void NoteStrokeEnd()
    {
        if (_demo is null || !_strokeInFlight) return;   // ignores Suppress: a Down must get its Up
        _strokeInFlight = false;
        Add(DemoEvent.Up(Now));
    }

    // ---- settings --------------------------------------------------------

    public void NoteTool(string tag)
    {
        if (Off || _strokeInFlight || tag == _lastTool) return;
        _lastTool = tag;
        Add(DemoEvent.Tool(Now, tag));
    }

    public void NoteColor(DemoColorSlot slot, uint bgra)
    {
        // A color-picker stroke sets the foreground as a *consequence* of a recorded gesture, so
        // recording it again during the stroke would be redundant on replay.
        if (Off || _strokeInFlight) return;
        if (_lastColor[(int)slot] == bgra) return;
        _lastColor[(int)slot] = bgra;
        Add(DemoEvent.Color(Now, slot, bgra));
    }

    public void NoteParam(DemoParam kind, int value)
    {
        if (Off || _lastParam[(int)kind] == value) return;
        _lastParam[(int)kind] = value;
        Add(DemoEvent.Param(Now, kind, value));
    }

    public void NoteParam(DemoParam kind, bool value) => NoteParam(kind, value ? 1 : 0);

    // ---- actions ---------------------------------------------------------

    public void NoteAction(string id)
    {
        if (Off) return;
        Add(DemoEvent.Action(Now, id));
    }

    public void NoteAction(string id, IReadOnlyList<double> args)
    {
        if (Off) return;
        Add(DemoEvent.Action(Now, id, args));
    }

    /// <summary>
    /// Records that a modal action ran without recording what the user chose inside its dialog.
    /// Playback surfaces these rather than pretending the session was continuous, because a demo
    /// that silently omits a Gaussian Blur diverges from the recording with no explanation.
    /// </summary>
    public void NoteSkipped(string label)
    {
        if (Off) return;
        Add(DemoEvent.Skipped(Now, label));
    }

    // ---- view ------------------------------------------------------------

    public void NoteView(double zoom, double originX, double originY)
    {
        if (Off) return;

        int now = Now;
        if (now - _lastViewMs < ViewIntervalMs && _demo!.Events.Count > 0 &&
            _demo.Events[^1].Op == DemoOp.View)
        {
            // Still inside the sampling window and the previous event is the view: overwrite it
            // rather than appending, so a pan drag costs one event per window, not one per frame.
            _demo.Events[^1] = DemoEvent.View(_demo.Events[^1].TimeMs, zoom, originX, originY);
            return;
        }

        _lastViewMs = now;
        Add(DemoEvent.View(now, zoom, originX, originY));
    }
}
