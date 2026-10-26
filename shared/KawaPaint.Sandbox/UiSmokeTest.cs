using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KawaPaint.App;
using KawaPaint.App.Core;
using KawaPaint.Engine;

namespace KawaPaint.Sandbox;

internal static class UiSmokeTest
{
    public static void RunAll()
    {
        SettingsStore.Current = new MemorySettingsStore();
        AppBuilder.Configure<KawaPaint.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont().SetupWithoutStarting();

        var failures = new List<string>();
        Check("document replacement during a stroke", DocumentReplacement);
        Check("undo during a stroke", UndoStroke);
        Check("layer and frame changes during a stroke", StrokeTargetChanges);
        Check("undo across animation frames", FrameHistory);
        Check("session follows canvas operations", SessionFollowsDocumentSwaps);
        Check("project save is atomic", ProjectSaveIsAtomic);
        Check("unhandled UI exceptions", UnhandledUiException);
        Check("modal shortcuts and focus", ModalInput);
        Check("inline text editing", InlineTextEditing);
        Check("adjustment cancellation", AdjustmentCancellation);
        Check("small dialog layout", DialogLayout);
        Check("narrow palette layout", PaletteLayout);
        Check("narrow menu layout", MenuLayout);
        if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
        Console.WriteLine("UI SMOKE OK - drawing history, modal input, adjustment cancellation and responsive layout");

        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine("UI PASS - " + name); }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("UI FAIL - " + failures[^1]); }
        }
    }

    private static void DocumentReplacement()
    {
        var canvas = new SurfaceView();
        var first = new Document(32, 32);
        first.AddLayer();
        canvas.SetDocument(first);
        canvas.BeginStroke(4, 4, false);
        var next = new Document(16, 16);
        next.AddLayer();
        canvas.SetDocument(next);
        canvas.ExtendStroke(8, 8);
        canvas.EndStroke();
        Assert(!canvas.History.CanUndo, "old stroke leaked into the new document's history");
        Assert(next.Layers[0].Surface[8, 8].A == 0, "old stroke painted the new document");
        canvas.BeginStroke(8, 8, false);
        canvas.EndStroke();
        Assert(canvas.History.CanUndo, "drawing did not resume after replacement");
        canvas.History.Clear();
        next.Dispose();
    }

    private static void InlineTextEditing()
    {
        WithView(900, 700, (view, window) =>
        {
            var canvas = view.FindControl<SurfaceView>("Canvas")!;
            var doc = new Document(400, 300);
            var layer = doc.AddLayer();
            canvas.SetDocument(doc);
            Pump();
            canvas.ZoomToFit();
            canvas.ZoomActual();
            Invoke(view, "SelectTool", "Text");
            var insertion = canvas.TranslatePoint(canvas.Origin + new Vector(30, 60) * canvas.Zoom, window)!.Value;
            window.MouseDown(insertion, MouseButton.Left);
            window.MouseUp(insertion, MouseButton.Left);
            Pump();
            var editor = Field<TextBox?>(view, "_textEditor") ?? throw new Exception("canvas click did not open text input");
            Assert(editor.IsFocused, "text input did not receive focus for its caret");
            Assert(editor.CaretBlinkInterval > TimeSpan.Zero, "text caret does not blink");
            window.KeyTextInput("Hello");
            PressKey(window, Key.Enter);
            window.KeyTextInput("world");
            Pump();
            Assert(editor.Text!.Replace("\r", "") == "Hello\nworld", "multiline typing did not stay in the editor");
            Assert(!canvas.History.CanUndo, "typing prematurely changed document history");
            PressKey(window, Key.D, RawInputModifiers.Control);
            Assert(canvas.CurrentTool is TextTool, "typing invoked an editor tool shortcut");
            canvas.ZoomIn();
            Pump();
            var handle = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TextMoveHandle");
            var start = handle.TranslatePoint(new Point(15, 15), window)!.Value;
            var delta = new Vector(40 * canvas.Zoom, 25 * canvas.Zoom);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + delta, RawInputModifiers.LeftMouseButton);
            window.MouseUp(start + delta, MouseButton.Left);
            Pump();
            Assert(Field<Point>(view, "_textPosition") == new Point(70, 85), "drag did not account for canvas zoom");
            Assert(editor.IsFocused, "dragging did not return focus to the text input");
            Screenshot(window, "inline-text");
            var finalize = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FinalizeTextButton");
            var buttonPoint = finalize.TranslatePoint(new Point(finalize.Bounds.Width / 2, 15), window)!.Value;
            window.MouseDown(buttonPoint, MouseButton.Left);
            window.MouseUp(buttonPoint, MouseButton.Left);
            Pump();
            Assert(Field<TextBox?>(view, "_textEditor") is null && canvas.History.CanUndo, "Finalize did not commit text");
            using var expected = new Surface(400, 300);
            TextOps.DrawText(expected, "Hello\nworld", 70, 85, 48, canvas.BrushColor);
            Assert(SamePixels(layer.Surface, expected), "finalized text did not match the moved position");
            canvas.Undo();
            using var blank = new Surface(400, 300);
            Assert(SamePixels(layer.Surface, blank) && !canvas.History.CanUndo, "text did not undo as one step");
            canvas.Redo();
            Assert(SamePixels(layer.Surface, expected), "redo did not restore finalized text");
            Invoke(view, "OnTextRequested", 10, 10);
            Pump();
            window.KeyTextInput("cancel me");
            PressKey(window, Key.Escape);
            Assert(Field<TextBox?>(view, "_textEditor") is null && SamePixels(layer.Surface, expected),
                "Escape changed image pixels");
            Invoke(view, "OnTextRequested", 0, 0);
            Pump();
            PressKey(window, Key.Enter, RawInputModifiers.Control);
            Assert(Field<TextBox?>(view, "_textEditor") is null && SamePixels(layer.Surface, expected),
                "finalizing empty text changed image pixels");
            Invoke(view, "OnTextRequested", 20, 20);
            Pump();
            window.KeyTextInput("small");
            var size = view.GetVisualDescendants().OfType<NumericUpDown>().Single(c => c.Name == "InlineTextSize");
            size.Value = 24;
            Pump();
            PressKey(window, Key.Enter, RawInputModifiers.Control);
            TextOps.DrawText(expected, "small", 20, 20, 24, canvas.BrushColor);
            Assert(SamePixels(layer.Surface, expected), "Ctrl+Enter did not commit the selected text size");
            Invoke(view, "OnTextRequested", 0, 0);
            Pump();
            window.KeyTextInput("new document");
            var replacement = new Document(20, 20);
            replacement.AddLayer();
            canvas.SetDocument(replacement);
            Pump();
            Assert(Field<TextBox?>(view, "_textEditor") is null && !canvas.History.CanUndo,
                "pending text survived document replacement");
        });
    }

    private static bool SamePixels(Surface first, Surface second)
    {
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first[x, y] != second[x, y]) return false;
        return true;
    }

    private static void ModalInput()
    {
        WithView(900, 700, (view, window) =>
        {
            var canvas = view.FindControl<SurfaceView>("Canvas")!;
            canvas.Selection!.SelectAll();
            Invoke(view, "ShowCanvasNewImageAsync");
            Pump();
            PressKey(window, Key.D, RawInputModifiers.Control);
            Assert(canvas.Selection.IsActive, "Ctrl+D deselected the canvas behind the dialog");
            var shade = Overlay(view);
            for (int i = 0; i < 30; i++)
            {
                PressKey(window, Key.Tab);
                var focused = window.FocusManager!.GetFocusedElement() as Visual;
                Assert(focused is not null && (focused == shade || shade.IsVisualAncestorOf(focused)),
                    "Tab escaped the dialog");
            }
            PressKey(window, Key.Escape);
            Assert(Field<Control?>(view, "_canvasDialogOverlay") is null, "Escape did not close dialog");
            Assert(canvas.IsEffectivelyEnabled, "editor stayed disabled after closing dialog");
            PressKey(window, Key.D, RawInputModifiers.Control);
            Assert(!canvas.Selection.IsActive, "editor shortcuts did not resume");
            Invoke(view, "ShowCanvasNewImageAsync");
            Pump();
            Overlay(view).Focus();
            PressKey(window, Key.Enter);
            Assert(Field<Control?>(view, "_canvasDialogOverlay") is null, "Enter did not accept dialog");
            Screenshot(window, "editor-desktop");
        });
    }

    private static void UndoStroke()
    {
        var canvas = new SurfaceView();
        using var document = new Document(32, 32);
        var layer = document.AddLayer();
        canvas.SetDocument(document);
        canvas.BeginStroke(4, 4, false);
        canvas.ExtendStroke(8, 8);
        canvas.Undo();
        Assert(layer.Surface[4, 4].A == 0 && layer.Surface[8, 8].A == 0,
            "Undo left the in-progress stroke on the canvas");
        Assert(canvas.History.CanRedo, "interrupted stroke cannot be redone");
        canvas.ExtendStroke(16, 16);
        canvas.EndStroke();
        Assert(layer.Surface[16, 16].A == 0, "pointer movement continued an undone stroke");
        canvas.Redo();
        Assert(layer.Surface[4, 4].A > 0 && layer.Surface[8, 8].A > 0, "Redo did not restore the stroke");
        canvas.History.Clear();
    }

    private static void StrokeTargetChanges()
    {
        var canvas = new SurfaceView();
        using var document = new Document(32, 32);
        var first = document.AddLayer("first");
        var second = document.AddLayer("second");
        var frameLayer = new Layer(32, 32, "next frame");
        document.AddFrame(new DocumentFrame([frameLayer]), makeActive: false);
        canvas.SetDocument(document);
        canvas.SetActiveLayer(first);
        canvas.BeginStroke(4, 4, false);
        canvas.SetActiveLayer(second);
        canvas.ExtendStroke(16, 16);
        Assert(first.Surface[4, 4].A > 0 && first.Surface[16, 16].A == 0 && second.Surface[16, 16].A == 0,
            "stroke continued after changing layers");
        canvas.BeginStroke(8, 8, false);
        canvas.SetActiveFrame(1);
        canvas.ExtendStroke(20, 20);
        canvas.EndStroke();
        Assert(second.Surface[8, 8].A > 0 && second.Surface[20, 20].A == 0 && frameLayer.Surface[20, 20].A == 0,
            "stroke continued after changing frames");
        canvas.History.Clear();
    }

    /// <summary>Issues #3, #6 and #13: frame edits are undoable, deleting a frame keeps undo of
    /// its earlier strokes working, and layer steps undo in the frame they were recorded in.</summary>
    private static void FrameHistory()
    {
        WithView(1100, 760, (view, _) =>
        {
            var canvas = view.FindControl<SurfaceView>("Canvas")!;
            var document = canvas.Document!;
            var args = new RoutedEventArgs();
            int baseLayers = document.LayerCount;

            // Delete a frame that has a stroke, then undo past the deletion into the stroke (#3).
            Invoke(view, "OnAddFrame", null!, args);
            Assert(document.FrameCount == 2 && document.ActiveFrameIndex == 1, "Add Frame did not add an active frame");
            var strokeLayer = canvas.ActiveLayer!;
            canvas.BeginStroke(4, 4, false);
            canvas.EndStroke();
            Assert(strokeLayer.Surface[4, 4].A > 0, "stroke did not paint the new frame");
            Invoke(view, "OnDeleteFrame", null!, args);
            Assert(document.FrameCount == 1, "Delete Frame did not remove the frame");
            canvas.Undo();
            Assert(document.FrameCount == 2 && document.ActiveFrameIndex == 1, "undo did not restore the deleted frame");
            canvas.Undo();
            Assert(strokeLayer.Surface[4, 4].A == 0, "undo did not remove the stroke from the restored frame");
            canvas.Undo();
            Assert(document.FrameCount == 1, "Add Frame could not be undone");
            canvas.Redo();
            canvas.Redo();
            Assert(document.FrameCount == 2 && strokeLayer.Surface[4, 4].A > 0, "redo did not bring back the frame and stroke");

            // A layer added in frame 1 must be removed from frame 1, even when undone from frame 2 (#6).
            canvas.SetActiveFrame(0);
            Invoke(view, "OnAddLayer", null!, args);
            var added = canvas.ActiveLayer!;
            Assert(document.LayerCount == baseLayers + 1, "Add Layer did not add a layer");
            canvas.SetActiveFrame(1);
            int otherLayers = document.LayerCount;
            canvas.Undo();
            Assert(document.ActiveFrameIndex == 0 && document.LayerCount == baseLayers,
                "layer undo did not act on the frame it was recorded in");
            Assert(document.Frames[1].Layers.Count == otherLayers, "layer undo changed another frame");
            canvas.SetActiveFrame(1);
            canvas.Redo();
            Assert(document.ActiveFrameIndex == 0 && document.LayerCount == baseLayers + 1 && document.Layers[^1] == added,
                "layer redo did not act on the frame it was recorded in");
            Assert(document.Frames[1].Layers.Count == otherLayers, "layer redo changed another frame");

            // Trimming history from another frame must not free a layer that frame 1 still uses.
            canvas.SetActiveFrame(1);
            canvas.History.MaxSteps = 1;
            canvas.BeginStroke(6, 6, false);
            canvas.EndStroke();
            canvas.History.MaxSteps = 0;
            GC.KeepAlive(added.Surface[0, 0]);   // throws if the layer was freed

            // Frame duration is an undoable step (#13).
            var duration = view.FindControl<NumericUpDown>("FrameDurationBox")!;
            int before = document.ActiveFrame.DurationMs;
            duration.Value = before + 150;
            Pump();
            Assert(document.ActiveFrame.DurationMs == before + 150, "duration box did not change the frame");
            canvas.Undo();
            Assert(document.ActiveFrame.DurationMs == before, "frame duration change could not be undone");
        });
    }

    /// <summary>Issue #4: autosave and saving must follow the document a canvas operation
    /// (crop, resize, flatten...) swaps in, and its undo.</summary>
    private static void SessionFollowsDocumentSwaps()
    {
        WithView(1100, 760, (view, _) =>
        {
            var canvas = view.FindControl<SurfaceView>("Canvas")!;
            var session = Field<DocumentSession>(view, "_session");
            session.SetGitProjectDirectory("/tmp/kawa-git-link");

            Invoke(view, "ApplyDocumentOp", "Flatten Image", (Func<Document, Document>)DocumentOps.Flatten);
            Assert(ReferenceEquals(session.Document, canvas.Document), "session kept the pre-flatten document");
            canvas.Undo();
            Assert(ReferenceEquals(session.Document, canvas.Document), "session did not follow undo of flatten");
            canvas.Redo();
            Assert(ReferenceEquals(session.Document, canvas.Document), "session did not follow redo of flatten");

            Invoke(view, "SetClean", new object?[] { null });
            Assert(ReferenceEquals(Field<DocumentSession>(view, "_session"), session),
                "saving after a canvas operation started a new session");
            Assert(session.GitProjectDirectory == "/tmp/kawa-git-link", "saving dropped the git project link");
        });
    }

    /// <summary>Issue #5: a project with a real path is written via temp file + move, never through
    /// the picker's (already truncated) stream; without one, the stream is only opened once the
    /// whole project is encoded.</summary>
    private static void ProjectSaveIsAtomic()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kawa_save_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "project" + DocumentFile.Extension);
        try
        {
            using var document = new Document(16, 16);
            document.AddLayer().Surface[3, 3] = ColorBgra.Black;
            var write = typeof(MainView).GetMethod("WriteProjectAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

            File.WriteAllText(path, "previous project");
            bool streamOpened = false;
            Func<Task<Stream>> picker = () => { streamOpened = true; return Task.FromResult<Stream>(new MemoryStream()); };
            Wait((Task)write.Invoke(null, [document, path, picker])!);
            Assert(!streamOpened, "project save with a real path wrote through the picker stream");
            using (var loaded = DocumentFile.Load(path))
                Assert(loaded.Layers[0].Surface[3, 3] == ColorBgra.Black, "saved project does not match the document");
            Assert(Directory.GetFiles(dir).Length == 1, "save left a temp file behind");

            var browserStream = new MemoryStream();
            Func<Task<Stream>> browser = () => Task.FromResult<Stream>(browserStream);
            Wait((Task)write.Invoke(null, [document, null, browser])!);
            using (var loaded = DocumentFile.Load(new MemoryStream(browserStream.ToArray())))   // closed by the save
                Assert(loaded.Layers[0].Surface[3, 3] == ColorBgra.Black, "browser-style save is not a valid project");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Issue #7: an exception escaping a UI handler is reported, not fatal.</summary>
    private static void UnhandledUiException()
    {
        CrashGuard.Install();
        WithView(1100, 760, (view, _) =>
        {
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("boom from a handler"));
            Pump();
            var status = view.FindControl<TextBlock>("StatusText")!;
            Assert(status.Text?.Contains("boom from a handler") == true, "unhandled exception was not reported");
        });
    }

    private static void DialogLayout()
    {
        WithView(320, 240, (view, window) =>
        {
            Invoke(view, "ShowCanvasTextAsync");
            Pump();
            var shade = Overlay(view);
            var card = ((Border)shade).Child!;
            Assert(card.Bounds.Width <= window.ClientSize.Width && card.Bounds.Height <= window.ClientSize.Height,
                $"dialog exceeds viewport: {card.Bounds.Size} in {window.ClientSize}");
            var text = shade.GetVisualDescendants().OfType<TextBox>().First();
            Assert(text.Bounds.Width <= window.ClientSize.Width, "text input overflows narrow dialog");
            text.Focus();
            PressKey(window, Key.Enter);
            Assert(Field<Control?>(view, "_canvasDialogOverlay") is not null, "Enter submitted multiline text");
            Screenshot(window, "dialog-small");
            var scroll = shade.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.ScrollToEnd();
            Pump();
            var ok = shade.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "OK"));
            var okOrigin = ok.TranslatePoint(default, window)!.Value;
            Assert(okOrigin.Y >= 0 && okOrigin.Y + ok.Bounds.Height <= window.ClientSize.Height,
                "dialog buttons cannot be reached by scrolling");
            PressKey(window, Key.Escape);
        });
    }

    private static void AdjustmentCancellation()
    {
        WithView(640, 480, (view, window) =>
        {
            var canvas = view.FindControl<SurfaceView>("Canvas")!;
            var before = canvas.ActiveLayer!.Surface[200, 200];
            var dialog = new AdjustmentDialog(canvas, "Invert", [], _ => new InvertEffect());
            Invoke(view, "ShowCanvasWindowContentAsync", dialog,
                (Action<Action<bool>>)dialog.UseCanvasHost,
                (Action)dialog.CancelCanvasHost, (Action)dialog.BeginCanvasHost);
            Pump();
            Assert(canvas.ActiveLayer.Surface[200, 200] != before, "adjustment preview did not apply");
            PressKey(window, Key.Escape);
            Assert(canvas.ActiveLayer.Surface[200, 200] == before, "cancel did not restore preview pixels");
            Assert(Field<Control?>(view, "_canvasDialogOverlay") is null && canvas.IsEffectivelyEnabled,
                "adjustment dialog left the editor blocked");
        });
    }

    private static void MenuLayout()
    {
        WithView(520, 700, (view, window) =>
        {
            var menu = view.GetVisualDescendants().OfType<Menu>().First();
            var toggle = view.FindControl<Avalonia.Controls.Primitives.ToggleButton>("ToggleTools")!;
            var toggleRect = new Rect(toggle.TranslatePoint(default, view)!.Value, toggle.Bounds.Size);
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                var rect = new Rect(item.TranslatePoint(default, view)!.Value, item.Bounds.Size);
                Assert(rect.Right <= window.ClientSize.Width && !rect.Intersects(toggleRect),
                    "menu overlaps panel buttons or extends outside the window");
            }
            Screenshot(window, "editor-narrow");
        });
    }

    private static void Screenshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("KAWAPAINT_UI_SCREENSHOTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static void PaletteLayout()
    {
        WithView(520, 700, (view, window) =>
        {
            var panel = view.FindControl<Border>("ColorsBorder")!;
            foreach (var button in panel.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
            {
                var origin = button.TranslatePoint(default, panel)!.Value;
                Assert(origin.X >= 0 && origin.X + button.Bounds.Width <= panel.Bounds.Width + 1,
                    $"palette button is outside panel: x={origin.X}, width={button.Bounds.Width}, panel={panel.Bounds.Width}");
            }
        });
    }

    private static void WithView(int width, int height, Action<MainView, Window> test)
    {
        var view = new MainView();
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Pump();
        try { test(view, window); }
        finally
        {
            window.Close();
            Pump();
            var canvas = view.FindControl<SurfaceView>("Canvas")!;
            canvas.History.Clear();
            canvas.Document?.Dispose();
        }
    }

    private static void PressKey(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Pump();
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the dispatcher until <paramref name="task"/> finishes: its continuations are
    /// posted back to this (UI) thread, so blocking on it would deadlock.</summary>
    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
        Assert(task.IsCompleted, "task did not finish");
        task.GetAwaiter().GetResult();
    }
    private static Control Overlay(MainView view) => Field<Control>(view, "_canvasDialogOverlay");
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
