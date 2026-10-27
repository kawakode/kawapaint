using System.Reflection;
using Pointer = Avalonia.Input.Pointer;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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
        Check("paste growing the canvas", PasteGrowingCanvas);
        Check("layout preset rename", LayoutPresetRename);
        Check("marching ants", MarchingAnts);
        Check("undo recomposites the changed area", PartialUndoComposite);
        Check("gesture backdrop", GestureBackdrop);
        Check("batch overwrite keeps the original on failure", BatchOverwriteFailure);
        Check("batch progress cancel and size memory warning", BatchProgressAndMemory);
        Check("resize by percentage and keep proportions", ResizeProportions);
        Check("selection changes are undoable", UndoableSelection);
        Check("rotate image dialog previews the new size", RotateImageDialogSize);
        Check("grow, shrink and feather selection", SelectionModify);
        Check("guides from rulers, snapping and pixel grid", RulerGuides);
        Check("painting on a 3D model", TexturePaintPanel);
        Check("one-finger drawing and two-finger zoom", TouchDrawing);
        Check("modal shortcuts and focus", ModalInput);
        Check("inline text editing", InlineTextEditing);
        Check("adjustment cancellation", AdjustmentCancellation);
        Check("small dialog layout", DialogLayout);
        Check("narrow palette layout", PaletteLayout);
        Check("narrow menu layout", MenuLayout);
        Check("tablet interface", TabletInterface);
        Check("phone interface", PhoneInterface);
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

    /// <summary>Issue #11: Paste (Grow Canvas) pastes into the active layer, which stays active.</summary>
    private static void PasteGrowingCanvas()
    {
        WithView(1100, 760, (view, _) =>
        {
            var canvas = view.FindControl<SurfaceView>("Canvas")!;
            Invoke(view, "OnAddLayer", null!, new Avalonia.Interactivity.RoutedEventArgs());
            var document = canvas.Document!;
            var bottom = document.Layers[0];
            canvas.SetActiveLayer(bottom);
            using var pasted = new Surface(document.Width + 40, document.Height + 10);
            pasted.Clear(ColorBgra.Black);

            Invoke(view, "PasteGrowingCanvas", pasted);

            var grown = canvas.Document!;
            Assert(grown.Width == pasted.Width && grown.Height == pasted.Height, "canvas did not grow");
            Assert(grown.Layers[0].Surface[pasted.Width - 1, 0] == ColorBgra.Black, "image was not pasted into the active layer");
            Assert(grown.Layers[^1].Surface[pasted.Width - 1, 0].A == 0, "image was pasted into the top layer");
            Assert(canvas.ActiveLayer == grown.Layers[0], "active layer changed");
        });
    }

    /// <summary>Issue #14: renaming a layout onto another layout's name must not replace it.</summary>
    private static void LayoutPresetRename()
    {
        WithView(1100, 760, (view, _) =>
        {
            var workspace = SettingsService.Instance.Settings.Workspace;
            string original = workspace.ActiveLayout;
            var other = new WorkspaceLayout();
            workspace.Layouts["Other"] = other;
            try
            {
                Invoke(view, "RenameLayoutPreset", original, "Other");
                Assert(workspace.Layouts["Other"] == other, "rename replaced the existing layout");
                Assert(workspace.Layouts.ContainsKey(original) && workspace.ActiveLayout == original,
                    "refused rename still changed the layouts");

                Invoke(view, "RenameLayoutPreset", original, "Renamed");
                Assert(workspace.Layouts.ContainsKey("Renamed") && !workspace.Layouts.ContainsKey(original)
                    && workspace.ActiveLayout == "Renamed", "a free name could not be used");
                Invoke(view, "RenameLayoutPreset", "Renamed", original);
            }
            finally { workspace.Layouts.Remove("Other"); }
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

    /// <summary>Undo and redo of a stroke recomposite only its tiles; the result must still match
    /// a full flatten, layers above and below included.</summary>
    private static void PartialUndoComposite()
    {
        var canvas = new SurfaceView();
        var document = new Document(300, 200);
        document.AddLayer().Surface.Clear(ColorBgra.FromBgra(40, 120, 200, 255));
        Layer painted = document.AddLayer();
        Layer above = document.AddLayer();
        above.Surface.ClearRect(0, 0, 300, 100, ColorBgra.FromBgra(0, 200, 0, 120));
        above.BlendMode = BlendMode.Multiply;
        canvas.SetDocument(document);
        canvas.SetActiveLayer(painted);
        canvas.CurrentTool = new PaintbrushTool();
        canvas.BrushWidth = 12;
        canvas.BeginStroke(20, 20, false);
        canvas.ExtendStroke(260, 170);
        canvas.EndStroke();

        AssertMatchesFlatten("after the stroke");
        canvas.Undo();
        AssertMatchesFlatten("after undo");
        canvas.Redo();
        AssertMatchesFlatten("after redo");
        canvas.History.Clear();
        document.Dispose();

        void AssertMatchesFlatten(string when)
        {
            var composite = Field<Surface>(canvas, "_composite");
            using Surface expected = document.Flatten();
            int differing = 0;
            for (int y = 0; y < expected.Height; y++)
                for (int x = 0; x < expected.Width; x++)
                    if (composite[x, y] != expected[x, y]) differing++;
            Assert(differing == 0, $"{differing} pixels differ from a full flatten {when}");
        }
    }

    /// <summary>A Move gesture on a middle layer composites over the flattened layers below it;
    /// every frame of the drag must still match a full flatten, even when a layer below changes
    /// mid-gesture.</summary>
    private static void GestureBackdrop()
    {
        var canvas = new SurfaceView();
        var document = new Document(240, 160);
        Layer bottom = document.AddLayer();
        bottom.Surface.Clear(ColorBgra.FromBgra(40, 120, 200, 255));
        Layer tint = document.AddLayer();
        tint.Surface.ClearRect(0, 40, 240, 60, ColorBgra.FromBgra(200, 30, 90, 140));
        Layer moved = document.AddLayer();
        moved.Surface.ClearRect(60, 50, 80, 50, ColorBgra.FromBgra(10, 220, 60, 200));
        Layer above = document.AddLayer();
        above.Surface.ClearRect(0, 0, 120, 160, ColorBgra.FromBgra(0, 200, 0, 120));
        above.BlendMode = BlendMode.Multiply;
        canvas.SetDocument(document);
        canvas.SetActiveLayer(moved);
        canvas.CurrentTool = new MoveTool();

        canvas.BeginStroke(100, 70, false);
        canvas.ExtendStroke(130, 90);
        AssertMatchesFlatten("mid-drag");
        Assert(Field<Surface?>(canvas, "_gestureBackdrop") is not null, "the drag built no backdrop");
        canvas.ExtendStroke(90, 60);
        AssertMatchesFlatten("after a second move");
        tint.Visible = false;
        canvas.ExtendStroke(95, 65);
        AssertMatchesFlatten("after hiding a layer below mid-drag");
        canvas.EndStroke();
        Assert(Field<Surface?>(canvas, "_gestureBackdrop") is null, "the backdrop outlived the gesture");
        AssertMatchesFlatten("after the drag");
        canvas.History.Clear();
        document.Dispose();

        void AssertMatchesFlatten(string when)
        {
            var composite = Field<Surface>(canvas, "_composite");
            using Surface expected = document.Flatten();
            int differing = 0;
            for (int y = 0; y < expected.Height; y++)
                for (int x = 0; x < expected.Width; x++)
                    if (composite[x, y] != expected[x, y]) differing++;
            Assert(differing == 0, $"{differing} pixels differ from a full flatten {when}");
        }
    }

    /// <summary>Issue #26: a batch result that cannot be encoded must leave the file it was going to
    /// overwrite exactly as it was, and a successful one must replace it completely.</summary>
    private static void RotateImageDialogSize()
    {
        var dialog = new RotateImageDialog(400, 200);
        var angle = dialog.GetLogicalDescendants().OfType<NumericUpDown>().Single(n => n.Name == "RotateAngle");
        var expand = dialog.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "RotateExpand");
        var size = dialog.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "RotateSize");
        angle.Value = 90;
        Assert(size.Text == "New size: 200 × 400" && dialog.ResultAngle == 90 && dialog.ResultExpand, "90°: " + size.Text);
        expand.IsChecked = false;
        Assert(size.Text == "New size: 400 × 200" && !dialog.ResultExpand, "kept canvas: " + size.Text);
    }

    private static void TexturePaintPanel()
    {
        WithView(1100, 760, (view, window) =>
        {
            var canvas = view.FindControl<SurfaceView>("Canvas")!;
            var doc = new Document(64, 64);
            Layer layer = doc.AddLayer();
            layer.Surface.ClearRect(0, 0, 32, 64, ColorBgra.FromBgra(40, 60, 220, 255));
            canvas.SetDocument(doc);
            canvas.SetActiveLayer(layer);
            canvas.RenderComposite();
            Pump();

            var mesh = KawaPaint.Engine.ThreeD.ObjMesh.Parse(new StringReader(
                "v -1 -1 0\nv 1 -1 0\nv 1 1 0\nv -1 1 0\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nf 1/1 2/2 3/3 4/4\n"));
            Invoke(view, "ShowPaintModel", mesh, "plane.obj");
            Pump();
            var preview = view.FindControl<ModelPreview>("Paint3DView")!;
            Assert(preview.IsEffectivelyVisible && preview.Bounds.Width > 100, "3D Paint panel did not open: " + preview.Bounds);
            preview.RenderModel();
            var centre = new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2);
            Assert(preview.TryGetTexCoord(centre, out var uv) && Math.Abs(uv.X - 0.5) < 0.15 && Math.Abs(uv.Y - 0.5) < 0.15,
                "the middle of the model should show the middle of the texture: " + uv);

            // A drag across the model paints the active layer as one undo step.
            canvas.BrushColor = ColorBgra.FromBgra(0, 200, 0, 255);
            canvas.BrushWidth = 6;
            int before = canvas.History.Count;
            var start = preview.TranslatePoint(centre - new Vector(20, 0), window)!.Value;
            var end = preview.TranslatePoint(centre + new Vector(20, 0), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Pump();
            Assert(layer.Surface[32, 32].G == 200, "painting on the model did not reach the texture centre: " + layer.Surface[32, 32]);
            Assert(canvas.History.Count == before + 1, "a paint drag should be one undo step");
            Screenshot(window, "texture-paint");
            canvas.Undo();
            Assert(layer.Surface[32, 32].A == 0, "undo did not remove the 3D paint");

            // The UV layout becomes a layer of its own.
            view.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "Paint3DUvLayoutBtn")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert(doc.LayerCount == 2 && doc.Layers[1].Name == "UV Layout", "UV layout layer was not added");
        });
    }

    private static void TouchDrawing()
    {
        WithView(900, 700, (view, window) =>
        {
            var canvas = view.FindControl<SurfaceView>("Canvas")!;
            var doc = new Document(400, 300);
            doc.AddLayer();
            canvas.SetDocument(doc);
            Pump();
            canvas.ZoomToFit();
            canvas.ZoomActual();
            canvas.TouchDrawingEnabled = true;
            canvas.TouchNavigationEnabled = true;
            Invoke(view, "SelectTool", "Pencil");
            Pump();
            Surface pixels = doc.Layers[0].Surface;
            ulong time = 1000;
            Point At(double x, double y) => canvas.TranslatePoint(canvas.Origin + new Vector(x, y) * canvas.Zoom, window)!.Value;
            var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            void Down(Pointer finger, Point p) =>
                canvas.RaiseEvent(new PointerPressedEventArgs(canvas, finger, window, p, time += 10, props, KeyModifiers.None));
            void Move(Pointer finger, Point p) =>
                canvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, canvas, finger, window, p, time += 50, props, KeyModifiers.None));
            void Up(Pointer finger, Point p) =>
                canvas.RaiseEvent(new PointerReleasedEventArgs(canvas, finger, window, p, time += 10,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                    KeyModifiers.None, MouseButton.Left));

            // One finger drags a line, starting where it first touched, as one undo step.
            int before = canvas.History.Count;
            var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
            Down(finger, At(20, 20));
            Move(finger, At(40, 20));
            Move(finger, At(60, 20));
            Up(finger, At(60, 20));
            Pump();
            Assert(pixels[20, 20].A > 0 && pixels[40, 20].A > 0 && pixels[60, 20].A > 0, "the finger stroke did not draw");
            Assert(canvas.History.Count == before + 1, $"the finger stroke made {canvas.History.Count - before} steps");

            // A tap draws a dot.
            var tap = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
            Down(tap, At(30, 80));
            Up(tap, At(30, 80));
            Pump();
            Assert(pixels[30, 80].A > 0, "a tap did not draw");

            // Two fingers zoom and leave the pixels alone, even though the first moved before the second landed.
            double zoom = canvas.Zoom;
            var a = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
            var b = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, false);
            Down(a, At(150, 150));
            time -= 45;   // the second finger follows within a few milliseconds
            Move(a, At(152, 150));
            Down(b, At(200, 150));
            Move(b, At(250, 150));
            Up(a, At(152, 150));
            Up(b, At(250, 150));
            Pump();
            Assert(canvas.Zoom > zoom * 1.3, $"pinch did not zoom: {zoom} -> {canvas.Zoom}");
            bool untouched = true;
            for (int y = 140; y < 160; y++)
                for (int x = 140; x < 260; x++)
                    untouched &= pixels[x, y].A == 0;
            Assert(untouched, "the pinch drew on the canvas");
        });
    }

    private static void WithInterface(string choice, int width, int height, Action<MainView, Window> test)
    {
        // SettingsService.Instance can be file-backed when another test class created it first,
        // so start from a fresh layout for the interface and leave the saved state as found.
        var workspace = SettingsService.Instance.Settings.Workspace;
        string before = workspace.Interface;
        bool rulers = workspace.ShowRulers;
        workspace.Interface = choice;
        workspace.Layouts.Remove(choice);
        try { WithView(width, height, test); }
        finally
        {
            workspace.Interface = before;
            workspace.ShowRulers = rulers;
            workspace.ActiveLayout = "Default";
            workspace.Layouts.Remove(choice);
            SettingsService.Instance.Save();
        }
    }

    private static void TabletInterface()
    {
        WithInterface("Tablet", 1280, 800, (view, window) =>
        {
            var doc = new Document(1600, 1000);
            doc.AddLayer().Surface.Clear(ColorBgra.White);
            view.FindControl<SurfaceView>("Canvas")!.SetDocument(doc);
            Pump();
            Assert(view.CurrentFormFactor == FormFactor.Tablet, "interface is " + view.CurrentFormFactor);
            var tools = view.FindControl<Border>("ToolsBorder")!;
            var layers = view.FindControl<Border>("LayersBorder")!;
            Assert(tools.IsEffectivelyVisible && tools.Bounds.X < 1, "tools are not docked left: " + tools.Bounds);
            Assert(layers.IsEffectivelyVisible && layers.Bounds.X > 700, "layers are not docked right: " + layers.Bounds);
            var toolButton = view.FindControl<StackPanel>("ToolPalette")!.GetLogicalDescendants().OfType<ToggleButton>().First();
            Assert(toolButton.Bounds.Width >= 40, "tool buttons are too small for a finger: " + toolButton.Bounds);
            Assert(view.FindControl<RulerBar>("HRuler")!.Bounds.Height < 1, "rulers are shown on a tablet");
            Screenshot(window, "tablet");
        });
    }

    private static void PhoneInterface()
    {
        WithInterface("Phone", 412, 915, (view, window) =>
        {
            var canvas = view.FindControl<SurfaceView>("Canvas")!;
            var doc = new Document(1080, 1440);
            doc.AddLayer().Surface.Clear(ColorBgra.White);
            canvas.SetDocument(doc);
            Pump();
            Assert(view.CurrentFormFactor == FormFactor.Phone, "interface is " + view.CurrentFormFactor);
            Assert(!view.FindControl<WrapPanel>("TopBar")!.IsVisible, "the desktop menu bar is still shown");

            // Tools in one row along the bottom, with undo/redo and the panels under them at the
            // bottom edge, and the canvas taking the full width above.
            var tools = view.FindControl<Border>("ToolsBorder")!;
            var toolsTop = tools.TranslatePoint(default, window)!.Value.Y;
            var thumbBar = view.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "ThumbBar");
            var thumbTop = thumbBar.TranslatePoint(default, window)!.Value.Y;
            Assert(thumbTop + thumbBar.Bounds.Height >= 914 && thumbTop > 820, $"the thumb bar is not at the bottom: {thumbTop}");
            Assert(toolsTop > 720 && toolsTop < thumbTop && tools.Bounds.Width > 400, $"tools are not a strip above the thumb bar: {toolsTop} {tools.Bounds}");
            Assert(view.FindControl<StackPanel>("QuickBar")!.Parent == thumbBar, "undo/redo did not move to the thumb bar");
            Assert(tools.Bounds.Height < 80, "the tool strip wraps: " + tools.Bounds);
            Assert(canvas.Bounds.Width > 400, "the canvas does not get the full width: " + canvas.Bounds);
            Assert(view.FindControl<Border>("OptionsBar")!.Bounds.Height < 70, "the tool options wrap");
            Screenshot(window, "phone");

            // A panel opens as a bottom sheet; a second one replaces it.
            var layersToggle = view.FindControl<ToggleButton>("ToggleLayers")!;
            layersToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            var layers = view.FindControl<Border>("LayersBorder")!;
            var sheet = layers.GetVisualAncestors().OfType<Border>().Last(b => b.Parent is Panel { Name: "FloatingLayer" });
            var sheetTop = sheet.TranslatePoint(default, window)!.Value.Y;
            Assert(layers.IsEffectivelyVisible && sheet.Bounds.Width >= 410 && sheet.Bounds.Bottom >= 913,
                $"layers did not open as a bottom sheet: {sheet.Bounds}");
            Assert(sheetTop > 300, "the sheet covers too much of the canvas: " + sheetTop);
            Screenshot(window, "phone-layers");

            view.FindControl<ToggleButton>("ToggleColorWheel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert(!layers.IsEffectivelyVisible && view.FindControl<Border>("ColorWheelBorder")!.IsEffectivelyVisible,
                "opening a second sheet did not replace the first");
            Screenshot(window, "phone-color");
            view.FindControl<ToggleButton>("ToggleColorWheel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();

            // ☰ opens the menus as a drawer; a section expands in place and its items work.
            var menuButton = view.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "MenuButton");
            Assert(menuButton.TranslatePoint(default, window)!.Value.Y < 20, "the menu button is not at the top");
            menuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            var drawer = view.GetVisualDescendants().OfType<Border>().SingleOrDefault(b => b.Name == "MainDrawer");
            Assert(drawer is not null && drawer.Bounds.Height > 900 && drawer.Bounds.X < 1, "the menu drawer did not open from the left");
            Button Row(string text) => drawer!.GetVisualDescendants().OfType<Button>()
                .First(b => b.Content is TextBlock t && t.Text!.Trim().TrimStart('▸', '▾', '✓').Trim() == text);
            Screenshot(window, "phone-drawer");
            Row("View").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Screenshot(window, "phone-drawer-view");
            var workspace = SettingsService.Instance.Settings.Workspace;
            bool rulers = workspace.ShowRulers;
            Row("Rulers").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert(!view.IsTouchOverlayOpen, "the drawer stayed open after picking an item");
            Assert(workspace.ShowRulers != rulers, "the drawer's Rulers item did nothing");

            // A long press opens the current tool's quick menu, and its slider sets the size.
            Invoke(view, "SelectTool", "Brush");
            Pump();
            var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
            var at = canvas.TranslatePoint(new Point(200, 400), window)!.Value;
            canvas.RaiseEvent(new PointerPressedEventArgs(canvas, finger, window, at, 1,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            // The headless dispatcher does not run timers on its own; fire the long-press one.
            Invoke(canvas, "CompleteLongPress");
            Pump();
            var quick = view.GetVisualDescendants().OfType<Border>().SingleOrDefault(b => b.Name == "QuickMenu");
            Assert(quick is not null, "a long press did not open the quick menu");
            var size = quick!.GetVisualDescendants().OfType<Slider>().Single(sl => sl.Name == "QuickSize");
            Assert(quick.GetVisualDescendants().OfType<Slider>().Any(sl => sl.Name == "QuickHardness"), "the brush's quick menu has no hardness");
            Screenshot(window, "phone-quick-menu");
            size.Value = 25;
            Pump();
            Assert(canvas.BrushWidth == 25, "the quick menu's size slider did not set the brush size: " + canvas.BrushWidth);
            canvas.RaiseEvent(new PointerReleasedEventArgs(canvas, finger, window, at, 2,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Pump();
            Assert(canvas.History.Count == 0, "the long press drew on the canvas");
        });
    }

    private static void RulerGuides()
    {
        WithView(900, 700, (view, window) =>
        {
            var canvas = view.FindControl<SurfaceView>("Canvas")!;
            var doc = new Document(400, 300);
            doc.AddLayer();
            canvas.SetDocument(doc);
            Pump();
            canvas.ZoomToFit();
            canvas.ZoomActual();
            canvas.ShowGuides = true;
            canvas.SnapToGuides = true;
            Pump();
            Point OnCanvas(double x, double y) => canvas.TranslatePoint(canvas.Origin + new Vector(x, y) * canvas.Zoom, window)!.Value;

            // Drag out of the top ruler: a horizontal guide lands where the pointer is released.
            var ruler = view.FindControl<RulerBar>("HRuler")!;
            var rulerPoint = ruler.TranslatePoint(new Point(ruler.Bounds.Width / 2, ruler.Bounds.Height / 2), window)!.Value;
            window.MouseDown(rulerPoint, MouseButton.Left);
            window.MouseMove(OnCanvas(100, 40), RawInputModifiers.LeftMouseButton);
            window.MouseMove(OnCanvas(100, 50), RawInputModifiers.LeftMouseButton);
            window.MouseUp(OnCanvas(100, 50), MouseButton.Left);
            Pump();
            Assert(canvas.Guides.Count == 1 && canvas.Guides.All[0] is { Horizontal: true, Position: 50 },
                "ruler drag did not make a guide at y=50: " + string.Join(",", canvas.Guides.All.Select(g => $"{g.Horizontal}:{g.Position}")));

            // A rectangle selection started 3 px off the guide snaps onto it.
            Invoke(view, "SelectTool", "RectSel");
            window.MouseDown(OnCanvas(20, 53), MouseButton.Left);
            window.MouseMove(OnCanvas(80, 120), RawInputModifiers.LeftMouseButton);
            window.MouseUp(OnCanvas(80, 120), MouseButton.Left);
            Pump();
            Assert(canvas.Selection!.GetBounds().Y == 50, "selection did not snap to the guide: " + canvas.Selection.GetBounds());

            // The Move tool picks the guide up; dropping it on the ruler removes it.
            Invoke(view, "SelectTool", "Move");
            window.MouseDown(OnCanvas(200, 50), MouseButton.Left);
            window.MouseMove(OnCanvas(200, 70), RawInputModifiers.LeftMouseButton);
            Assert(canvas.DraggedGuide is { Position: 70 }, "Move tool did not drag the guide");
            window.MouseMove(rulerPoint, RawInputModifiers.LeftMouseButton);
            window.MouseUp(rulerPoint, MouseButton.Left);
            Pump();
            Assert(canvas.Guides.Count == 0 && canvas.Selection.GetBounds().Y == 50, "guide was not removed, or the drag moved pixels");

            canvas.Guides.Add(horizontal: false, 200);
            doc.Layers[0].Surface.ClearRect(190, 140, 20, 20, ColorBgra.FromBgra(40, 160, 230, 255));
            canvas.RenderComposite();
            while (canvas.Zoom < SurfaceView.PixelGridMinZoom) canvas.ZoomIn();
            Pump();
            Assert(canvas.Zoom >= SurfaceView.PixelGridMinZoom, "zoom did not reach the pixel grid: " + canvas.Zoom);
            Assert(canvas.ShowGuides && canvas.Guides.Count == 1, $"guides hidden? {canvas.ShowGuides} {canvas.Guides.Count}");
            Screenshot(window, "pixel-grid");
        });
    }

    private static void SelectionModify()
    {
        var previews = new List<int>();
        var dialog = new SelectionRadiusDialog("Grow Selection", 4, previews.Add);
        var radius = dialog.GetLogicalDescendants().OfType<NumericUpDown>().Single(n => n.Name == "SelectionRadius");
        radius.Value = 7;
        Assert(previews.LastOrDefault() == 7 && dialog.ResultRadius == 7, "radius change did not preview: " + string.Join(",", previews));
        radius.Value = 9999;
        Assert(dialog.ResultRadius == Selection.MaxModifyRadius, "radius was not capped: " + dialog.ResultRadius);

        var canvas = new SurfaceView();
        var document = new Document(80, 80);
        document.AddLayer();
        canvas.SetDocument(document);
        canvas.ChangeSelection("Select", () => canvas.Selection!.ReplaceWithRectangle(30, 30, 50, 50));
        canvas.ChangeSelection("Grow Selection", () => canvas.Selection!.Grow(5));
        Assert(canvas.Selection!.GetBounds() == (25, 25, 30, 30), "grow: " + canvas.Selection.GetBounds());
        canvas.ChangeSelection("Feather Selection", () => canvas.Selection!.Feather(4));
        canvas.Undo();
        canvas.Undo();
        Assert(canvas.Selection.GetBounds() == (30, 30, 20, 20) && canvas.Selection.CoverageAt(30, 40) == 255,
            "undo did not restore the square: " + canvas.Selection.GetBounds());
        canvas.History.Clear();
        document.Dispose();
    }

    private static void UndoableSelection()
    {
        var canvas = new SurfaceView();
        var document = new Document(100, 80);
        Layer layer = document.AddLayer();
        layer.Surface.ClearRect(10, 10, 20, 20, ColorBgra.FromBgra(0, 0, 255, 255));
        canvas.SetDocument(document);
        canvas.SetActiveLayer(layer);
        Selection Sel() => canvas.Selection!;

        canvas.CurrentTool = new RectSelectTool();
        canvas.BeginStroke(10, 10, false);
        canvas.ExtendStroke(30, 30);
        canvas.EndStroke();
        Assert(Sel().IsActive && Sel().IsSelected(15, 15) && !Sel().IsSelected(50, 50), "rectangle selection not made");
        int afterSelect = canvas.History.Count;

        canvas.ChangeSelection("Invert Selection", () => Sel().Invert());
        Assert(Sel().IsSelected(50, 50) && !Sel().IsSelected(15, 15), "invert did not apply");
        canvas.Undo();
        Assert(Sel().IsSelected(15, 15) && !Sel().IsSelected(50, 50), "undo did not restore the rectangle");
        canvas.Undo();
        Assert(!Sel().IsActive, "undo did not clear the first selection");
        canvas.Redo();
        Assert(Sel().IsSelected(15, 15) && !Sel().IsSelected(50, 50), "redo did not bring the rectangle back");
        canvas.Redo();
        Assert(Sel().IsSelected(50, 50), "redo did not re-invert");
        canvas.Undo();

        // Moving selected pixels changes pixels and outline: one step undoes both.
        canvas.CurrentTool = new MoveTool();
        canvas.BeginStroke(15, 15, false);
        canvas.ExtendStroke(55, 45);
        canvas.EndStroke();
        Assert(canvas.History.Count == afterSelect + 1, $"the move made {canvas.History.Count - afterSelect} steps, expected 1");
        Assert(Sel().IsSelected(55, 45) && layer.Surface[55, 45].A == 255, "the move did not carry pixels and outline");
        canvas.Undo();
        Assert(Sel().IsSelected(15, 15) && !Sel().IsSelected(55, 45) && layer.Surface[15, 15].A == 255
               && layer.Surface[55, 45].A == 0, "one undo did not put back both pixels and outline");

        // A brush stroke inside the selection leaves the selection out of its step.
        canvas.CurrentTool = new PaintbrushTool();
        canvas.BeginStroke(12, 12, false);
        canvas.ExtendStroke(28, 28);
        canvas.EndStroke();
        Assert(canvas.History.NextUndoName == "Paintbrush", $"stroke step is '{canvas.History.NextUndoName}'");
        canvas.Undo();
        Assert(Sel().IsSelected(15, 15), "undoing a stroke changed the selection");
        canvas.History.Clear();
        document.Dispose();
    }

    private static void ResizeProportions()
    {
        var dialog = new ResizeDialog(400, 200);
        NumericUpDown Field(string name) => dialog.GetLogicalDescendants().OfType<NumericUpDown>().Single(n => n.Name == name);
        var percent = Field("ResizePercent");
        var width = Field("ResizeWidth");
        var height = Field("ResizeHeight");
        var keep = dialog.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "ResizeKeepProportions");
        string State() => $"{percent.Value}% {width.Value}x{height.Value}";

        percent.Value = 50;
        Assert(dialog.ResultWidth == 200 && dialog.ResultHeight == 100, "50 % did not halve both sides: " + State());
        height.Value = 50;
        Assert(dialog.ResultWidth == 100 && percent.Value == 25, "height did not carry the width and percentage: " + State());
        keep.IsChecked = false;
        width.Value = 300;
        Assert(dialog.ResultHeight == 50 && percent.Value is null, "unlocked width still moved the height: " + State());
        keep.IsChecked = true;
        Assert(dialog.ResultWidth == 300 && dialog.ResultHeight == 150 && percent.Value == 75,
            "re-locking did not restore the ratio: " + State());
    }

    private static void BatchProgressAndMemory()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var progress = new BatchProgressDialog("Batch", 3);
            _ = progress.ShowDialog(owner);
            Pump();
            progress.Report(1, "b.png");
            Pump();
            Assert(progress.FindDescendantOfType<ProgressBar>()!.Value == 1, "progress did not advance");
            progress.Close();
            Pump();
            Assert(progress.Token.IsCancellationRequested, "closing the progress window did not cancel the run");
            Assert(progress.IsVisible, "the progress window closed before the run stopped");
            progress.Finish();
            Pump();
            Assert(!progress.IsVisible, "Finish did not close the progress window");

            var width = new NumericUpDown { Minimum = 1, Maximum = 20000, Value = 100 };
            var height = new NumericUpDown { Minimum = 1, Maximum = 20000, Value = 100 };
            var ok = new Button();
            var note = new ImageMemoryNote(width, height, ok, layers: 1);
            Assert(ok.IsEnabled && note.Text.Text!.Contains("MB"), $"small image flagged: {note.Text.Text}");
            width.Value = 20000;
            height.Value = 20000;
            bool fits = KawaPaint.Engine.ImageMemory.Check(KawaPaint.Engine.ImageMemory.Estimate(20000, 20000, 1),
                KawaPaint.Engine.ImageMemory.Budget) != KawaPaint.Engine.ImageMemoryFit.TooLarge;
            Assert(note.Text.Text!.Contains("6.0 GB") && ok.IsEnabled == fits,
                $"20000 x 20000 not reported: {note.Text.Text}, OK enabled {ok.IsEnabled}");
        }
        finally { owner.Close(); }
    }

    private static void BatchOverwriteFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kawapaint-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var window = new Window();
        window.Show();
        try
        {
            using var document = new Document(8, 8);
            document.AddLayer().Surface.Clear(ColorBgra.FromBgra(0, 0, 255, 255));
            byte[] original = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();

            // No codec handles this extension, so encoding throws.
            string unsupported = Path.Combine(directory, "picture.nocodec");
            File.WriteAllBytes(unsupported, original);
            var target = StorageFile(window, unsupported);
            bool threw = false;
            try { Wait(SaveBatchResult(document, target)); }
            catch (Exception) { threw = true; }
            Assert(threw, "encoding to an unknown format did not fail");
            Assert(File.ReadAllBytes(unsupported).SequenceEqual(original), "the original was changed by a failed batch save");

            string png = Path.Combine(directory, "picture.png");
            File.WriteAllBytes(png, original);
            Wait(SaveBatchResult(document, StorageFile(window, png)));
            using var reread = File.OpenRead(png);
            using var decoded = KawaPaint.Engine.Codecs.CodecRegistry.Decode(reread, "picture.png");
            Assert(decoded.Width == 8 && decoded[3, 3] == ColorBgra.FromBgra(0, 0, 255, 255), "the overwrite did not hold the new image");
            Assert(Directory.GetFiles(directory).Length == 2, "a temp file was left behind");
        }
        finally
        {
            window.Close();
            Pump();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task SaveBatchResult(Document document, Avalonia.Platform.Storage.IStorageFile target)
    {
        var save = typeof(MainView).GetMethod("SaveBatchResultAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (Task)save.Invoke(null, new object?[] { document, target, null })!;
    }

    private static Avalonia.Platform.Storage.IStorageFile StorageFile(Window window, string path)
    {
        var task = window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
        Wait(task);
        return task.Result ?? throw new Exception("the headless storage provider cannot open " + path);
    }

    /// <summary>The ants are 1px lines on the selection's edge, at any zoom, and they move.</summary>
    private static void MarchingAnts()
    {
        var canvas = new SurfaceView();
        var document = new Document(200, 100);
        document.AddLayer().Surface.Clear(ColorBgra.FromBgra(128, 128, 128, 255));
        var window = new Window { Width = 400, Height = 200, Content = canvas };
        window.Show();
        try
        {
            canvas.SetDocument(document);
            Pump();
            canvas.Selection!.ReplaceWithRectangle(50, 20, 150, 80);
            canvas.NotifySelectionChanged();
            Pump();

            using var first = Frame();
            typeof(SurfaceView).GetField("_antPhase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvas, 2);
            canvas.InvalidateVisual();
            Pump();
            using var second = Frame();

            // The document is fitted at zoom 1 and centred, so image (50, 20) lands at (150, 70).
            int edgeAnts = 0, moved = 0, inside = 0;
            for (int x = 160; x < 240; x++)
            {
                var a = Pixel(first, x, 70);
                if (a.R is < 16 or > 240 && a.R == a.G) edgeAnts++;
                if (a != Pixel(second, x, 70)) moved++;
                if (Pixel(first, x, 100).R is < 16 or > 240) inside++;
            }
            Assert(edgeAnts == 80, $"edge pixels drawn as ants: {edgeAnts}/80");
            Assert(moved > 0, "ants did not move when the phase advanced");
            Assert(inside == 0, "ants were drawn inside the selection");
            Screenshot(window, "marching-ants");
        }
        finally
        {
            window.Close();
            Pump();
            document.Dispose();
        }

        Avalonia.Media.Imaging.Bitmap Frame() => window.CaptureRenderedFrame()!;
    }

    private static Avalonia.Media.Color Pixel(Avalonia.Media.Imaging.Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        unsafe
        {
            fixed (byte* p = buffer)
                bitmap.CopyPixels(new PixelRect(x, y, 1, 1), (nint)p, 4, 4);
        }
        return Avalonia.Media.Color.FromArgb(buffer[3], buffer[2], buffer[1], buffer[0]);
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
