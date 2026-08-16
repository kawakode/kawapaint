using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using KawaPaint.Engine;

namespace KawaPaint.App;

public partial class MainWindow : Window
{
    private bool _suppress;   // guards programmatic updates to layer-panel controls
    private bool _dirty;
    private bool _forceClose;
    private string? _currentPath;   // set once a .kwp path is known

    public MainWindow()
    {
        InitializeComponent();

        BlendCombo.ItemsSource = Enum.GetValues<BlendMode>();
        Canvas.DocumentChanged += (_, _) => RebuildLayerPanel();
        Canvas.PrimaryColorPicked += OnColorPicked;
        Canvas.TextRequested += OnTextRequested;
        Canvas.ZoomChanged += z => { if (ZoomText is not null) ZoomText.Text = $"{z * 100:0}%"; };
        KeyDown += OnKeyDown;

        LoadDemoDocument();
        Canvas.History.Changed += (_, _) => MarkDirty();
        SetClean(null);
    }

    // ---- unsaved-changes tracking ----------------------------------------

    private void MarkDirty() { _dirty = true; UpdateTitle(); }

    private void SetClean(string? path) { _dirty = false; _currentPath = path; UpdateTitle(); }

    private void UpdateTitle()
    {
        string name = _currentPath is null ? "untitled" : System.IO.Path.GetFileName(_currentPath);
        Title = (_dirty ? "* " : "") + name + " — KawaPaint";
    }

    /// <summary>Returns true if it's OK to proceed (saved or discarded); false if the user cancelled.</summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_dirty) return true;
        var choice = await new ConfirmSaveDialog("Save changes to the current image before continuing?")
            .ShowDialog<SaveChoice>(this);
        return choice switch
        {
            SaveChoice.Save => await SaveProjectAsync(),
            SaveChoice.Discard => true,
            _ => false
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_forceClose || !_dirty) return;
        e.Cancel = true;
        _ = HandleCloseAsync();
    }

    private async Task HandleCloseAsync()
    {
        if (await ConfirmDiscardAsync()) { _forceClose = true; Close(); }
    }

    // ---- documents --------------------------------------------------------

    private void LoadDemoDocument()
    {
        var doc = new Document(800, 600);

        var bg = doc.AddLayer("Background");
        unsafe
        {
            for (int y = 0; y < doc.Height; y++)
            {
                ColorBgra* row = (ColorBgra*)bg.Surface.GetRowPointer(y);
                for (int x = 0; x < doc.Width; x++)
                    row[x] = ColorBgra.FromBgra((byte)(x * 255 / doc.Width), (byte)(y * 255 / doc.Height), 80, 255);
            }
        }

        var overlay = doc.AddLayer("Overlay");
        var red = ColorBgra.FromBgra(0, 0, 220, 200);
        for (int y = 120; y < 400; y++)
            for (int x = 160; x < 520; x++)
                overlay.Surface[x, y] = red;

        Canvas.SetDocument(doc);
        StatusText.Text = "Demo document — left-drag to draw, wheel zoom, middle/right-drag pan, Ctrl+Z undo";
    }

    private async void OnNew(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync()) return;
        var doc = new Document(800, 600);
        doc.AddLayer("Background").Surface.Clear(ColorBgra.White);
        Canvas.SetDocument(doc);
        SetClean(null);
        StatusText.Text = "New 800×600 document";
    }

    private async void OnOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        try
        {
            string path = file.Path.LocalPath;
            using var loaded = Surface.Load(path);
            var doc = new Document(loaded.Width, loaded.Height);
            var layer = doc.AddLayer(System.IO.Path.GetFileNameWithoutExtension(path));
            layer.Surface.CopyFrom(loaded);
            Canvas.SetDocument(doc);
            SetClean(null);   // imported image has no project path yet
            StatusText.Text = $"{System.IO.Path.GetFileName(path)} — {loaded.Width}×{loaded.Height}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Open failed: " + ex.Message;
        }
    }

    private async void OnOpenProject(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open KawaPaint project",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("KawaPaint project") { Patterns = new[] { "*" + DocumentFile.Extension } }
            }
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        try
        {
            string path = file.Path.LocalPath;
            var doc = DocumentFile.Load(path);
            Canvas.SetDocument(doc);
            SetClean(path);
            StatusText.Text = $"{System.IO.Path.GetFileName(path)} — {doc.LayerCount} layer(s)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Open project failed: " + ex.Message;
        }
    }

    private async void OnSaveProject(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await SaveProjectAsync();

    /// <summary>Saves the project (to the known path, or prompts). Returns true if saved.</summary>
    private async Task<bool> SaveProjectAsync()
    {
        if (Canvas.Document is null) return false;

        string? path = _currentPath;
        if (path is null || !path.EndsWith(DocumentFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save KawaPaint project",
                DefaultExtension = DocumentFile.Extension.TrimStart('.'),
                SuggestedFileName = "untitled" + DocumentFile.Extension
            });
            if (file is null) return false;
            path = file.Path.LocalPath;
        }

        try
        {
            DocumentFile.Save(Canvas.Document, path);
            SetClean(path);
            StatusText.Text = "Saved project " + System.IO.Path.GetFileName(path);
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save project failed: " + ex.Message;
            return false;
        }
    }

    private async void OnSaveAs(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas.Document is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save flattened image as PNG",
            DefaultExtension = "png",
            SuggestedFileName = "untitled.png"
        });
        if (file is null) return;

        try
        {
            using var flat = Canvas.Document.Flatten();
            flat.Save(file.Path.LocalPath);
            StatusText.Text = "Saved " + System.IO.Path.GetFileName(file.Path.LocalPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save failed: " + ex.Message;
        }
    }

    private void OnEffect(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;
        var layer = Canvas.ActiveLayer;
        if (layer is null) return;

        KawaPaint.Engine.IEffect fx = tag switch
        {
            "invert" => new InvertEffect(),
            "gray" => new GrayscaleEffect(),
            "sepia" => new SepiaEffect(),
            "brighten" => new BrightnessContrastEffect(25, 1.0),
            "darken" => new BrightnessContrastEffect(-25, 1.0),
            "contrast" => new BrightnessContrastEffect(0, 1.3),
            "blur" => new BoxBlurEffect(6),
            "sharpen" => new SharpenEffect(),
            "emboss" => new EmbossEffect(),
            "edge" => new EdgeDetectEffect(),
            "autolevels" => new AutoLevelsEffect(),
            _ => new InvertEffect()
        };

        var snapshot = layer.Surface.Clone();
        fx.Apply(layer.Surface);
        if (Canvas.Selection is { IsActive: true }) Canvas.Selection.Clip(layer.Surface, snapshot);
        Canvas.History.Push(LayerSurfaceMemento.FromSnapshot(layer, snapshot, fx.Name));
        Canvas.RenderComposite();
        Canvas.InvalidateVisual();
        StatusText.Text = "Applied: " + fx.Name + " (to " + layer.Name + ")";
    }

    private async void OnResize(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null) return;
        var dlg = new ResizeDialog(doc.Width, doc.Height);
        if (await dlg.ShowDialog<bool>(this))
        {
            int w = dlg.ResultWidth, h = dlg.ResultHeight;
            if (w == doc.Width && h == doc.Height) return;
            Canvas.SetDocument(DocumentOps.Resize(doc, w, h));
            MarkDirty();
            StatusText.Text = $"Resized to {w}×{h}";
        }
    }

    private void OnCropToSelection(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null || Canvas.Selection is not { IsActive: true } sel) { StatusText.Text = "Crop needs an active selection"; return; }

        var (x, y, w, h) = sel.GetBounds();
        if (w <= 0 || h <= 0) return;
        Canvas.SetDocument(DocumentOps.Crop(doc, x, y, w, h));
        StatusText.Text = $"Cropped to {w}×{h}";
    }

    private void OnFlatten(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null || doc.LayerCount <= 1) return;
        Canvas.SetDocument(DocumentOps.Flatten(doc));
        StatusText.Text = "Flattened";
    }

    private void OnSelectNone(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Canvas.Selection?.SelectNone();
        Canvas.NotifySelectionChanged();
        StatusText.Text = "Selection cleared";
    }

    private async void OnAdjust(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag || Canvas.ActiveLayer is null) return;

        AdjustmentDialog dlg = tag switch
        {
            "bc" => new AdjustmentDialog(Canvas, "Brightness / Contrast", new[]
            {
                new AdjustmentDialog.SliderSpec("Brightness", -100, 100, 0, "0"),
                new AdjustmentDialog.SliderSpec("Contrast", 0.5, 2.0, 1.0, "0.00")
            }, v => new BrightnessContrastEffect((int)v[0], v[1])),

            "hsl" => new AdjustmentDialog(Canvas, "Hue / Saturation", new[]
            {
                new AdjustmentDialog.SliderSpec("Hue", -180, 180, 0, "0"),
                new AdjustmentDialog.SliderSpec("Saturation", 0, 2, 1, "0.00"),
                new AdjustmentDialog.SliderSpec("Lightness", -1, 1, 0, "0.00")
            }, v => new HueSaturationEffect(v[0], v[1], v[2])),

            "levels" => new AdjustmentDialog(Canvas, "Levels", new[]
            {
                new AdjustmentDialog.SliderSpec("In Black", 0, 254, 0, "0"),
                new AdjustmentDialog.SliderSpec("In White", 1, 255, 255, "0"),
                new AdjustmentDialog.SliderSpec("Gamma", 0.1, 3.0, 1.0, "0.00")
            }, v => new LevelsEffect((int)v[0], (int)v[1], v[2])),

            "posterize" => new AdjustmentDialog(Canvas, "Posterize", new[]
            {
                new AdjustmentDialog.SliderSpec("Levels", 2, 16, 4, "0")
            }, v => new PosterizeEffect((int)v[0])),

            "noise" => new AdjustmentDialog(Canvas, "Add Noise", new[]
            {
                new AdjustmentDialog.SliderSpec("Amount", 0, 100, 25, "0")
            }, v => new NoiseEffect((int)v[0])),

            _ => new AdjustmentDialog(Canvas, "Gaussian Blur", new[]
            {
                new AdjustmentDialog.SliderSpec("Radius", 1, 30, 5, "0")
            }, v => new BoxBlurEffect((int)v[0]))
        };

        await dlg.ShowDialog(this);
        StatusText.Text = dlg.Title ?? "Adjustment";
    }

    private void OnUndo(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.Undo();
    private void OnRedo(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.Redo();
    private void OnZoomIn(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.ZoomIn();
    private void OnZoomOut(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.ZoomOut();
    private void OnZoomFit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.ZoomToFit();
    private void OnZoomActual(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.ZoomActual();

    private void OnExit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    // ---- toolbar ----------------------------------------------------------

    private void OnColor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hex) return;
        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte bl = Convert.ToByte(hex.Substring(5, 2), 16);
        ColorPick.Color = Color.FromRgb(r, g, bl);   // fires OnPickColor
    }

    private void OnPickColor(object? sender, Avalonia.Controls.ColorChangedEventArgs e)
    {
        if (Canvas is null) return;
        Color c = e.NewColor;
        Canvas.BrushColor = ColorBgra.FromBgra(c.B, c.G, c.R, c.A);
    }

    private void OnPickColor2(object? sender, Avalonia.Controls.ColorChangedEventArgs e)
    {
        if (Canvas is null) return;
        Color c = e.NewColor;
        Canvas.SecondaryColor = ColorBgra.FromBgra(c.B, c.G, c.R, c.A);
    }

    private void OnTool(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) SelectTool(tag);
    }

    private void SelectTool(string tag)
    {
        ITool tool = tag switch
        {
            "Eraser" => new EraserTool(),
            "Fill" => new PaintBucketTool(),
            "Pick" => new ColorPickerTool(),
            "Line" => new LineTool(),
            "Rect" => new RectangleTool(),
            "Ellipse" => new EllipseTool(),
            "Gradient" => new GradientTool(),
            "Text" => new TextTool(),
            "Move" => new MoveTool(),
            "RectSel" => new RectSelectTool(),
            "EllipseSel" => new EllipseSelectTool(),
            "Lasso" => new LassoSelectTool(),
            _ => new PencilTool()
        };
        Canvas.CurrentTool = tool;
        StatusText.Text = "Tool: " + tool.Name;
    }

    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.KeyModifiers == Avalonia.Input.KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Avalonia.Input.Key.OemPlus:
                case Avalonia.Input.Key.Add: Canvas.ZoomIn(); e.Handled = true; break;
                case Avalonia.Input.Key.OemMinus:
                case Avalonia.Input.Key.Subtract: Canvas.ZoomOut(); e.Handled = true; break;
                case Avalonia.Input.Key.D0: Canvas.ZoomToFit(); e.Handled = true; break;
                case Avalonia.Input.Key.D1: Canvas.ZoomActual(); e.Handled = true; break;
            }
            return;
        }

        // Ignore when typing into a control (e.g. a text field gets focus).
        if (e.KeyModifiers != Avalonia.Input.KeyModifiers.None) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;

        string? tag = e.Key switch
        {
            Avalonia.Input.Key.P => "Pencil",
            Avalonia.Input.Key.E => "Eraser",
            Avalonia.Input.Key.F => "Fill",
            Avalonia.Input.Key.K => "Pick",
            Avalonia.Input.Key.L => "Line",
            Avalonia.Input.Key.R => "Rect",
            Avalonia.Input.Key.O => "Ellipse",
            Avalonia.Input.Key.G => "Gradient",
            Avalonia.Input.Key.T => "Text",
            Avalonia.Input.Key.M => "Move",
            Avalonia.Input.Key.S => "RectSel",
            _ => null
        };
        if (tag is not null) { SelectTool(tag); e.Handled = true; }
    }

    private async void OnTextRequested(int x, int y)
    {
        var layer = Canvas.ActiveLayer;
        if (layer is null) return;

        var dlg = new TextDialog();
        bool ok = await dlg.ShowDialog<bool>(this);
        if (!ok || string.IsNullOrEmpty(dlg.ResultText)) return;

        var snapshot = layer.Surface.Clone();
        TextOps.DrawText(layer.Surface, dlg.ResultText, x, y, dlg.ResultSize, Canvas.BrushColor);
        if (Canvas.Selection is { IsActive: true }) Canvas.Selection.Clip(layer.Surface, snapshot);
        Canvas.History.Push(LayerSurfaceMemento.FromSnapshot(layer, snapshot, "Text"));
        Canvas.RenderComposite();
        Canvas.InvalidateVisual();
        StatusText.Text = "Added text";
    }

    private void OnColorPicked(ColorBgra c)
    {
        ColorPick.Color = Color.FromArgb(c.A, c.R, c.G, c.B);
        StatusText.Text = $"Picked {c}";
    }

    private void OnSize(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int size = (int)Math.Round(e.NewValue);
        if (Canvas is not null) Canvas.BrushWidth = size;
        if (SizeLabel is not null) SizeLabel.Text = size + " px";
    }

    private void OnAntialias(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas is not null && AntialiasCheck is not null) Canvas.Antialias = AntialiasCheck.IsChecked ?? true;
    }

    private void OnFillShapes(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas is not null && FillShapesCheck is not null) Canvas.FillShapes = FillShapesCheck.IsChecked ?? false;
    }

    private void OnTolerance(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int tol = (int)Math.Round(e.NewValue);
        if (Canvas is not null) Canvas.FillTolerance = tol;
        if (ToleranceLabel is not null) ToleranceLabel.Text = tol.ToString();
    }

    // ---- layers panel -----------------------------------------------------

    private void RebuildLayerPanel()
    {
        var doc = Canvas.Document;
        if (doc is null) return;

        _suppress = true;

        LayerList.Items.Clear();
        // Top layer first (matches how layers stack visually).
        for (int i = doc.LayerCount - 1; i >= 0; i--)
        {
            var layer = doc.Layers[i];

            var check = new CheckBox { IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center };
            var capturedLayer = layer;
            check.IsCheckedChanged += (_, _) =>
            {
                if (_suppress) return;
                bool now = check.IsChecked ?? true;
                capturedLayer.Visible = now;
                Canvas.History.Push(new DelegateMemento("Toggle Visibility",
                    undo: () => capturedLayer.Visible = !now,
                    redo: () => capturedLayer.Visible = now));
                Canvas.RenderComposite();
                Canvas.InvalidateVisual();
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(check);
            panel.Children.Add(new TextBlock { Text = layer.Name, VerticalAlignment = VerticalAlignment.Center });

            LayerList.Items.Add(new ListBoxItem { Content = panel, Tag = layer });
        }

        // Sync selection + property controls to the active layer.
        var active = Canvas.ActiveLayer;
        if (active is not null)
        {
            foreach (ListBoxItem item in LayerList.Items.Cast<ListBoxItem>())
                if (ReferenceEquals(item.Tag, active)) { LayerList.SelectedItem = item; break; }

            BlendCombo.SelectedItem = active.BlendMode;
            OpacitySlider.Value = active.Opacity;
        }

        _suppress = false;
    }

    private void OnLayerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (LayerList.SelectedItem is ListBoxItem { Tag: Layer layer })
            Canvas.SetActiveLayer(layer);
    }

    private void RefreshDocument()
    {
        Canvas.RenderComposite();
        Canvas.InvalidateVisual();
        RebuildLayerPanel();
    }

    private void OnAddLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null) return;
        var layer = doc.AddLayer();
        Canvas.SetActiveLayer(layer);

        Canvas.History.Push(new DelegateMemento("Add Layer",
            undo: () => { doc.RemoveLayer(layer); Canvas.SetActiveLayer(doc.Layers[^1]); },
            redo: () => { doc.AddLayer(layer); Canvas.SetActiveLayer(layer); }));

        RefreshDocument();
    }

    private void OnDeleteLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        var active = Canvas.ActiveLayer;
        if (doc is null || active is null || doc.LayerCount <= 1) return;

        int idx = doc.IndexOf(active);
        doc.RemoveLayer(active);   // not disposed: undo may restore it
        Canvas.SetActiveLayer(doc.Layers[Math.Clamp(idx, 0, doc.LayerCount - 1)]);

        Canvas.History.Push(new DelegateMemento("Delete Layer",
            undo: () => { doc.InsertLayer(idx, active); Canvas.SetActiveLayer(active); },
            redo: () => { doc.RemoveLayer(active); Canvas.SetActiveLayer(doc.Layers[Math.Clamp(idx, 0, doc.LayerCount - 1)]); }));

        RefreshDocument();
    }

    private void OnDuplicateLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        var active = Canvas.ActiveLayer;
        if (doc is null || active is null) return;

        int idx = doc.IndexOf(active);
        var dup = active.Clone();
        doc.InsertLayer(idx + 1, dup);
        Canvas.SetActiveLayer(dup);

        Canvas.History.Push(new DelegateMemento("Duplicate Layer",
            undo: () => { doc.RemoveLayer(dup); Canvas.SetActiveLayer(active); },
            redo: () => { doc.InsertLayer(idx + 1, dup); Canvas.SetActiveLayer(dup); }));

        RefreshDocument();
    }

    private void OnMergeDown(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        var active = Canvas.ActiveLayer;
        if (doc is null || active is null) return;
        int idx = doc.IndexOf(active);
        if (idx <= 0) { StatusText.Text = "Nothing below to merge into"; return; }

        var below = doc.Layers[idx - 1];
        var belowBefore = below.Surface.Clone();
        LayerOps.MergeInto(below, active);
        doc.RemoveLayer(active);
        Canvas.SetActiveLayer(below);

        Canvas.History.Push(new DelegateMemento("Merge Down",
            undo: () => { below.Surface.CopyFrom(belowBefore); doc.InsertLayer(idx, active); Canvas.SetActiveLayer(active); },
            redo: () => { LayerOps.MergeInto(below, active); doc.RemoveLayer(active); Canvas.SetActiveLayer(below); }));

        RefreshDocument();
    }

    private void MoveActive(int delta)
    {
        var doc = Canvas.Document;
        var active = Canvas.ActiveLayer;
        if (doc is null || active is null) return;
        int from = doc.IndexOf(active);
        int to = from + delta;
        if (to < 0 || to >= doc.LayerCount) return;
        doc.MoveLayer(from, to);

        Canvas.History.Push(new DelegateMemento("Reorder Layer",
            undo: () => doc.MoveLayer(to, from),
            redo: () => doc.MoveLayer(from, to)));

        RefreshDocument();
    }

    private void OnLayerUp(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => MoveActive(+1);
    private void OnLayerDown(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => MoveActive(-1);

    private void OnBlendChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Canvas.ActiveLayer is null) return;
        if (BlendCombo.SelectedItem is BlendMode mode)
        {
            Canvas.ActiveLayer.BlendMode = mode;
            Canvas.RenderComposite();
            Canvas.InvalidateVisual();
        }
    }

    private void OnOpacityChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppress || Canvas?.ActiveLayer is null) return;
        Canvas.ActiveLayer.Opacity = (byte)Math.Round(e.NewValue);
        Canvas.RenderComposite();
        Canvas.InvalidateVisual();
    }
}
