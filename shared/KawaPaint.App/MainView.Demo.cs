// KawaPaint - demo recording and playback, wired into the editor.
//
// This is the half of the demo system that knows what an editor action *is*: DemoRecorder and
// DemoPlayer only move opcodes around. Two directions to keep straight:
//
//   record - SurfaceView pushes real pointer gestures here, CommandRegistry.DispatchScope catches
//            every command however it was fired, and the handful of menu handlers that are not
//            commands call RecordAction/RecordSkipped directly.
//   replay - ApplyDemoEvent turns an opcode back into the *same* call the user's input made. It
//            deliberately goes through the real handlers (including a synthetic sender carrying
//            the Tag a menu item would have had) rather than reimplementing them, because a
//            second implementation is a second thing to keep in step.
//
// What is not captured, and why: anything whose result lives in a modal dialog or outside the
// document (effect parameters, resize dimensions, the text tool's string, the system clipboard,
// file pickers). Those are recorded as Skipped so playback can say so out loud instead of
// silently diverging from what the user saw.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using KawaPaint.App.Core.Demo;
using KawaPaint.App.Core.Scripting;
using KawaPaint.Engine;

namespace KawaPaint.App;

public partial class MainView
{
    private readonly DemoRecorder _demoRecorder = new();
    private readonly DemoPlayer _demoPlayer = new();
    private readonly ScriptRecorder _scriptRecorder = new();

    /// <summary>Non-parameterised replay actions that are not CommandRegistry commands.</summary>
    private Dictionary<string, Action>? _demoActions;

    /// <summary>Last pointer position seen during playback, for the replay cursor overlay.</summary>
    private double _demoCursorX, _demoCursorY;

    /// <summary>Appended to the playback readout when the demo came from a different build.</summary>
    private string _demoVersionNote = "";

    private static readonly FilePickerFileType DemoFileType = new("KawaPaint demo")
    {
        Patterns = new[] { "*" + DemoFile.Extension }
    };

    public bool IsPlayingDemo => _demoPlayer.IsActive;

    // ---- wiring ----------------------------------------------------------

    /// <summary>Called from the constructor once the AXAML tree and Canvas exist.</summary>
    private void InitializeDemo()
    {
        Canvas.StrokeBegan += (x, y, ctrl) => _demoRecorder.NoteStrokeBegin(x, y, ctrl);
        Canvas.StrokeExtended += (x, y) => _demoRecorder.NoteStrokeMove(x, y);
        Canvas.StrokeEnded += () => _demoRecorder.NoteStrokeEnd();
        Canvas.ViewChanged += () => _demoRecorder.NoteView(Canvas.Zoom, Canvas.Origin.X, Canvas.Origin.Y);

        // One tap covers menu items, keyboard shortcuts and dock buttons alike, and suppresses the
        // duplicate note the command's own handler would make while it runs. Demo and script
        // recording are mutually exclusive at the UI level (see OnScriptRecord/OnDemoRecord), but
        // the scope returned here still has to cover whichever one (or, defensively, both) is
        // live - returning only one recorder's scope would leave the other unsuppressed and double-
        // record the command.
        _commands.DispatchScope = command =>
        {
            IDisposable? demoScope = null, scriptScope = null;

            if (_demoRecorder.IsRecording)
            {
                if (IsDialogCommand(command.Id)) _demoRecorder.NoteSkipped(command.Label);
                else _demoRecorder.NoteAction(command.Id);
                demoScope = _demoRecorder.Suppress();
            }

            if (_scriptRecorder.IsRecording)
            {
                _scriptRecorder.NoteAction(command.Id);
                scriptScope = _scriptRecorder.Suppress();
            }

            return demoScope is null && scriptScope is null ? null : new CombinedScope(demoScope, scriptScope);
        };

        _demoActions = BuildDemoActions();

        _demoPlayer.Progressed += UpdateDemoStatus;
        _demoPlayer.SpeedChanged += UpdateDemoStatus;
        _demoPlayer.Finished += OnDemoFinished;
        _demoRecorder.Progress += UpdateDemoStatus;
    }

    /// <summary>
    /// Commands whose real work happens in a modal dialog or a file picker, or which depend on
    /// state the demo file does not carry (the system clipboard). Recorded, never replayed.
    /// </summary>
    private static bool IsDialogCommand(string id) => id switch
    {
        "file.new" or "file.open" or "file.openProject" or "file.saveProject"
            or "file.export" or "file.linkGitProject" => true,
        "edit.paste" or "edit.pasteIntoNewLayer" or "edit.pasteIntoNewImage" => true,
        _ => false
    };

    private Dictionary<string, Action> BuildDemoActions()
    {
        var empty = new RoutedEventArgs();

        return new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            ["image.flipH"] = () => OnFlipH(this, empty),
            ["image.flipV"] = () => OnFlipV(this, empty),
            ["image.rotateCW"] = () => OnRotateCW(this, empty),
            ["image.rotateCCW"] = () => OnRotateCCW(this, empty),
            ["image.crop"] = () => OnCropToSelection(this, empty),
            ["image.flatten"] = () => OnFlatten(this, empty),

            ["layer.add"] = () => OnAddLayer(this, empty),
            ["layer.delete"] = () => OnDeleteLayer(this, empty),
            ["layer.duplicate"] = () => OnDuplicateLayer(this, empty),
            ["layer.mergeDown"] = () => OnMergeDown(this, empty),
            ["layer.up"] = () => OnLayerUp(this, empty),
            ["layer.down"] = () => OnLayerDown(this, empty),

            ["color.swap"] = () => OnSwapColors(this, empty),
            ["history.clear"] = () => OnClearHistory(this, empty),
            ["view.rulers.toggle"] = () => OnToggleRulers(this, empty)
        };
    }

    private sealed class CombinedScope : IDisposable
    {
        private readonly IDisposable? _a, _b;
        public CombinedScope(IDisposable? a, IDisposable? b) { _a = a; _b = b; }
        public void Dispose() { _a?.Dispose(); _b?.Dispose(); }
    }

    // ---- recording taps used by the handlers in MainView.axaml.cs --------

    private void RecordAction(string id)
    {
        _demoRecorder.NoteAction(id);
        _scriptRecorder.NoteAction(id);   // no-ops for ids outside ScriptRecorder.IsScriptable
    }

    private void RecordSkipped(string label) => _demoRecorder.NoteSkipped(label);

    /// <summary>Records a parametric effect step (committed AdjustmentDialog values) for the script
    /// stream only - the demo recorder already logged this as skipped via RecordSkipped, since the
    /// demo format has no way to carry the dialog's values.</summary>
    private void RecordScriptAction(string id, double[] args) => _scriptRecorder.NoteAction(id, args);

    private void RecordTool(string tag) => _demoRecorder.NoteTool(tag);

    private void RecordParam(DemoParam kind, int value) => _demoRecorder.NoteParam(kind, value);

    private void RecordParam(DemoParam kind, bool value) => _demoRecorder.NoteParam(kind, value);

    private void RecordColor(DemoColorSlot slot, ColorBgra color) => _demoRecorder.NoteColor(slot, color.Bgra);

    // ---- start / stop recording ------------------------------------------

    private void OnDemoRecord(object? sender, RoutedEventArgs e)
    {
        if (_demoRecorder.IsRecording) { _ = StopAndSaveDemoAsync(); return; }

        if (_demoPlayer.IsActive) { StatusText.Text = "Stop playback before recording"; return; }
        if (Canvas.Document is not { } doc) return;

        var (start, fill) = CaptureDemoStart(doc);
        _demoRecorder.Start(doc.Width, doc.Height, start, fill,
                            typeof(MainView).Assembly.GetName().Version?.ToString() ?? "");

        // Seed the stream with the state a replay has to begin from, since the recorder only ever
        // sees *changes* after this point and a demo that starts mid-session would otherwise
        // inherit whatever the playing machine happened to have selected.
        _demoRecorder.NoteTool(_currentToolTag);
        _demoRecorder.NoteColor(DemoColorSlot.Foreground, Canvas.BrushColor.Bgra);
        _demoRecorder.NoteColor(DemoColorSlot.Background, Canvas.SecondaryColor.Bgra);
        _demoRecorder.NoteParam(DemoParam.BrushSize, Canvas.BrushWidth);
        _demoRecorder.NoteParam(DemoParam.Hardness, CurrentHardnessPercent);
        _demoRecorder.NoteParam(DemoParam.Tolerance, Canvas.FillTolerance);
        _demoRecorder.NoteParam(DemoParam.Antialias, Canvas.Antialias);
        _demoRecorder.NoteParam(DemoParam.FillShapes, Canvas.FillShapes);
        _demoRecorder.NoteParam(DemoParam.GlobalFill, Canvas.GlobalFill);
        _demoRecorder.NoteParam(DemoParam.SelectionCombineMode, (int)Canvas.SelectionCombineMode);
        _demoRecorder.NoteView(Canvas.Zoom, Canvas.Origin.X, Canvas.Origin.Y);

        UpdateDemoMenuState();
        StatusText.Text = "Recording demo - Demo ▸ Stop & Save to finish";
    }

    /// <summary>
    /// The starting document, as .kwp bytes. A canvas that is one plain layer of a single colour -
    /// what File ▸ New leaves behind, and where most demos start - is described by that colour
    /// instead, which is the difference between a couple of kilobytes and none at all.
    /// </summary>
    private static (byte[]? Document, uint Fill) CaptureDemoStart(Document doc)
    {
        if (TryUniformFill(doc, out uint fill)) return (null, fill);

        using var ms = new MemoryStream();
        DocumentFile.Save(doc, ms);
        return (ms.ToArray(), 0);
    }

    private static bool TryUniformFill(Document doc, out uint bgra)
    {
        bgra = 0;
        if (doc.LayerCount != 1) return false;

        var layer = doc.Layers[0];
        if (!layer.Visible || layer.Opacity != 255 || layer.BlendMode != BlendMode.Normal) return false;

        var surface = layer.Surface;
        uint first = surface[0, 0].Bgra;
        for (int y = 0; y < surface.Height; y++)
            for (int x = 0; x < surface.Width; x++)
                if (surface[x, y].Bgra != first) return false;

        bgra = first;
        return true;
    }

    private async Task StopAndSaveDemoAsync()
    {
        var demo = _demoRecorder.Stop();
        UpdateDemoMenuState();
        if (demo is null) return;

        if (demo.Events.Count == 0) { StatusText.Text = "Nothing was recorded"; return; }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Demo",
            SuggestedFileName = "demo" + DemoFile.Extension,
            DefaultExtension = DemoFile.Extension.TrimStart('.'),
            FileTypeChoices = new[] { DemoFileType }
        });
        if (file is null) { StatusText.Text = "Demo discarded"; return; }

        demo.Title = file.Name;

        try
        {
            long bytes;
            await using (var stream = await file.OpenWriteAsync())
            {
                demo.Save(stream);
                bytes = stream.CanSeek ? stream.Position : 0;
            }

            string summary = $"Demo saved: {demo.StrokeCount} strokes, {demo.Events.Count} events, "
                           + FormatDuration(demo.DurationMs);
            StatusText.Text = bytes > 0 ? $"{summary}, {bytes / 1024.0:0.#} KB" : summary;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Demo save failed: " + ex.Message;
        }
    }

    // ---- playback --------------------------------------------------------

    private async void OnDemoPlay(object? sender, RoutedEventArgs e)
    {
        if (_demoRecorder.IsRecording) { StatusText.Text = "Stop recording before playback"; return; }
        if (_demoPlayer.IsActive) { StatusText.Text = "A demo is already playing"; return; }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Play Demo",
            AllowMultiple = false,
            FileTypeFilter = new[] { DemoFileType }
        });
        if (files.Count == 0) return;

        DemoFile demo;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            demo = DemoFile.Load(stream);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not read demo: " + ex.Message;
            return;
        }

        // Replaying inputs onto whatever is on screen would paint over the user's work, so the
        // demo's own starting document replaces it - the same discard prompt New/Open use.
        if (!await ConfirmDiscardAsync()) return;

        try
        {
            Canvas.SetDocument(BuildDemoStartDocument(demo));
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not restore the demo's starting image: " + ex.Message;
            return;
        }

        SetClean(null);
        RebuildLayerPanel();

        // The replay owns the canvas gesture state for its whole run; live pointer input would be
        // fed into the stroke it is midway through drawing.
        Canvas.SuppressPointerInput = true;
        _demoPlayer.Play(demo, ApplyDemoEvent);
        UpdateDemoMenuState();

        // The format version is checked on load; the app version is not, because an older demo
        // usually still replays fine. Note the mismatch rather than staying silent, so a replay
        // that does come out wrong has an obvious first thing to suspect. It rides on the demo
        // status line rather than StatusText, which the demo's own first event overwrites at once.
        string mine = typeof(MainView).Assembly.GetName().Version?.ToString() ?? "";
        _demoVersionNote = demo.AppVersion.Length > 0 && demo.AppVersion != mine
            ? $"  (recorded by {demo.AppVersion}, this build is {mine})"
            : "";
        StatusText.Text = "Playing " + files[0].Name;
    }

    private static Document BuildDemoStartDocument(DemoFile demo)
    {
        if (demo.StartDocument is { Length: > 0 } bytes)
        {
            using var ms = new MemoryStream(bytes, writable: false);
            return DocumentFile.Load(ms);
        }

        var doc = new Document(Math.Max(1, demo.CanvasWidth), Math.Max(1, demo.CanvasHeight));
        var layer = doc.AddLayer("Background");
        var fill = default(ColorBgra);
        fill.Bgra = demo.BlankFill;
        layer.Surface.Clear(fill);
        return doc;
    }

    private void OnDemoPausePlayback(object? sender, RoutedEventArgs e)
    {
        if (!_demoPlayer.IsActive) return;
        _demoPlayer.TogglePause();
        UpdateDemoMenuState();
    }

    private void OnDemoStopPlayback(object? sender, RoutedEventArgs e)
    {
        if (!_demoPlayer.IsActive) return;
        _demoPlayer.Stop();
    }

    private void OnDemoSpeed(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && double.TryParse(tag,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double speed))
            _demoPlayer.Speed = speed;
        UpdateDemoMenuState();
    }

    private void OnDemoFinished()
    {
        // A demo cut short mid-stroke would leave the canvas holding an uncommitted gesture.
        Canvas.EndStroke();
        Canvas.SetDemoCursor(null, null, false);
        Canvas.SuppressPointerInput = false;
        UpdateDemoMenuState();
        UpdateDemoStatus();
    }

    /// <summary>Turns one recorded opcode back into the editor action that produced it.</summary>
    private void ApplyDemoEvent(DemoEvent e)
    {
        switch (e.Op)
        {
            case DemoOp.PointerDown:
                _demoCursorX = e.X; _demoCursorY = e.Y;
                Canvas.SetDemoCursor(e.X, e.Y, true);
                Canvas.BeginStroke(e.X, e.Y, e.A != 0);
                break;

            case DemoOp.PointerMove:
                _demoCursorX = e.X; _demoCursorY = e.Y;
                Canvas.SetDemoCursor(e.X, e.Y, true);
                Canvas.ExtendStroke(e.X, e.Y);
                break;

            case DemoOp.PointerUp:
                Canvas.EndStroke();
                Canvas.SetDemoCursor(_demoCursorX, _demoCursorY, false);
                break;

            case DemoOp.Tool:
                if (e.Text is { Length: > 0 } tag) SelectTool(tag);
                break;

            case DemoOp.Color:
            {
                var color = default(ColorBgra);
                color.Bgra = e.Value;
                if ((DemoColorSlot)e.A == DemoColorSlot.Foreground) SetForeground(color);
                else SetBackground(color);
                break;
            }

            case DemoOp.Param:
                ApplyDemoParam((DemoParam)e.A, e.B);
                break;

            case DemoOp.View:
                Canvas.SetView(e.X, e.Y, e.Z);
                break;

            case DemoOp.Action:
                if (e.Text is { Length: > 0 } id) RunDemoAction(id);
                break;

            case DemoOp.Skipped:
                StatusText.Text = "Demo: skipped " + e.Text + " (dialog input isn't recorded)";
                break;

        }
    }

    private void ApplyDemoParam(DemoParam kind, int value)
    {
        switch (kind)
        {
            case DemoParam.BrushSize: ApplyBrushSize(value); break;
            case DemoParam.Hardness: ApplyBrushHardness(value); break;
            case DemoParam.Tolerance: ApplyTolerance(value); break;

            case DemoParam.Antialias:
                Canvas.Antialias = value != 0;
                if (AntialiasCheck is not null) AntialiasCheck.IsChecked = value != 0;
                break;

            case DemoParam.FillShapes:
                Canvas.FillShapes = value != 0;
                if (FillShapesCheck is not null) FillShapesCheck.IsChecked = value != 0;
                break;

            case DemoParam.GlobalFill:
                Canvas.GlobalFill = value != 0;
                if (GlobalFillCheck is not null) GlobalFillCheck.IsChecked = value != 0;
                break;

            case DemoParam.SelectionCombineMode:
                if (Enum.IsDefined(typeof(SelectionCombineMode), value))
                    ApplySelectionCombineMode((SelectionCombineMode)value);
                break;
        }
    }

    /// <summary>
    /// Dispatches a recorded action id. Parameterised ids ("layer.select.2") are parsed here;
    /// everything else is either a table entry or a registry command.
    /// </summary>
    private void RunDemoAction(string id)
    {
        if (_demoActions!.TryGetValue(id, out var run)) { run(); return; }

        // Menu handlers that read their Tag off the sender are replayed through a stand-in
        // MenuItem, so the real handler runs unchanged rather than being duplicated here.
        if (TrySplit(id, "effect.", out string arg)) { OnEffect(TagSender(arg), new RoutedEventArgs()); return; }
        if (TrySplit(id, "view.rulerUnit.", out arg)) { OnSetRulerUnit(TagSender(arg), new RoutedEventArgs()); return; }

        if (TrySplit(id, "layer.select.", out arg) && int.TryParse(arg, out int index))
        {
            if (Canvas.Document is { } doc && index >= 0 && index < doc.LayerCount)
            {
                Canvas.SetActiveLayer(doc.Layers[index]);
                RebuildLayerPanel();
            }
            return;
        }

        if (TrySplit(id, "layer.visible.", out arg))
        {
            string[] parts = arg.Split('.');
            if (parts.Length == 2 && int.TryParse(parts[0], out int li) &&
                Canvas.Document is { } doc && li >= 0 && li < doc.LayerCount)
            {
                doc.Layers[li].Visible = parts[1] == "1";
                Canvas.RenderComposite();
                Canvas.InvalidateVisual();
                RebuildLayerPanel();
            }
            return;
        }

        if (TrySplit(id, "layer.reorder.", out arg))
        {
            string[] parts = arg.Split('.');
            if (parts.Length == 2 && int.TryParse(parts[0], out int from) && int.TryParse(parts[1], out int to) &&
                Canvas.Document is { } doc &&
                from >= 0 && from < doc.LayerCount && to >= 0 && to < doc.LayerCount)
            {
                doc.MoveLayer(from, to);
                Canvas.RenderComposite();
                Canvas.InvalidateVisual();
                RebuildLayerPanel();
            }
            return;
        }

        if (TrySplit(id, "layer.blend.", out arg) && Enum.TryParse<BlendMode>(arg, out var mode))
        {
            if (Canvas.ActiveLayer is { } active)
            {
                active.BlendMode = mode;
                Canvas.RenderComposite();
                Canvas.InvalidateVisual();
                RebuildLayerPanel();
            }
            return;
        }

        if (TrySplit(id, "layer.opacity.", out arg) && byte.TryParse(arg, out byte opacity))
        {
            if (Canvas.ActiveLayer is { } active)
            {
                active.Opacity = opacity;
                Canvas.RenderComposite();
                Canvas.InvalidateVisual();
                RebuildLayerPanel();
            }
            return;
        }

        if (TrySplit(id, "history.jump.", out arg) && int.TryParse(arg, out int position))
        {
            Canvas.JumpToHistory(position);
            return;
        }

        if (_commands.Find(id) is not null) { _commands.Execute(id); return; }

        StatusText.Text = "Demo: unknown action '" + id + "' - skipped";
    }

    private static bool TrySplit(string id, string prefix, out string rest)
    {
        if (id.StartsWith(prefix, StringComparison.Ordinal)) { rest = id[prefix.Length..]; return true; }
        rest = "";
        return false;
    }

    private static MenuItem TagSender(string tag) => new() { Tag = tag };

    // ---- status / menu state ---------------------------------------------

    private void UpdateDemoStatus()
    {
        if (_demoRecorder.IsRecording)
        {
            DemoStatusText.Text = $"● REC  {FormatDuration(_demoRecorder.ElapsedMs)}  ({_demoRecorder.EventCount} events)";
            return;
        }

        if (_demoPlayer.IsActive)
        {
            string state = _demoPlayer.IsPlaying ? "▶" : "❚❚";
            DemoStatusText.Text =
                $"{state} {FormatDuration(_demoPlayer.PositionMs)} / {FormatDuration(_demoPlayer.DurationMs)}  ×{_demoPlayer.Speed:0.##}{_demoVersionNote}";
            return;
        }

        DemoStatusText.Text = "";
    }

    private static string FormatDuration(int ms) => TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss");

    private void UpdateDemoMenuState()
    {
        bool recording = _demoRecorder.IsRecording;
        bool playing = _demoPlayer.IsActive;

        DemoRecordItem.Header = recording ? "Stop & _Save Demo…" : "_Record Demo";
        DemoRecordItem.IsEnabled = !playing;
        DemoPlayItem.IsEnabled = !recording && !playing;
        DemoPauseItem.IsEnabled = playing;
        // "Resume" only makes sense for a demo that is loaded but stopped; with nothing playing at
        // all the greyed-out item should read as the action it will offer next, not the last one.
        DemoPauseItem.Header = !playing || _demoPlayer.IsPlaying ? "_Pause" : "_Resume";
        DemoStopItem.IsEnabled = playing;

        UpdateDemoStatus();
    }
}
