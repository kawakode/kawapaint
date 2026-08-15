using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using KawaPaint.Engine;

namespace KawaPaint.App;

public partial class MainWindow : Window
{
    private bool _suppress;   // guards programmatic updates to layer-panel controls

    public MainWindow()
    {
        InitializeComponent();

        BlendCombo.ItemsSource = Enum.GetValues<BlendMode>();
        Canvas.DocumentChanged += (_, _) => RebuildLayerPanel();
        Canvas.PrimaryColorPicked += OnColorPicked;
        Canvas.TextRequested += OnTextRequested;

        LoadDemoDocument();
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

    private void OnNew(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = new Document(800, 600);
        doc.AddLayer("Background").Surface.Clear(ColorBgra.White);
        Canvas.SetDocument(doc);
        StatusText.Text = "New 800×600 document";
    }

    private async void OnOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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
            StatusText.Text = $"{System.IO.Path.GetFileName(path)} — {loaded.Width}×{loaded.Height}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Open failed: " + ex.Message;
        }
    }

    private async void OnOpenProject(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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
            StatusText.Text = $"{System.IO.Path.GetFileName(path)} — {doc.LayerCount} layer(s)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Open project failed: " + ex.Message;
        }
    }

    private async void OnSaveProject(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas.Document is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save KawaPaint project",
            DefaultExtension = DocumentFile.Extension.TrimStart('.'),
            SuggestedFileName = "untitled" + DocumentFile.Extension
        });
        if (file is null) return;

        try
        {
            DocumentFile.Save(Canvas.Document, file.Path.LocalPath);
            StatusText.Text = "Saved project " + System.IO.Path.GetFileName(file.Path.LocalPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save project failed: " + ex.Message;
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
        if (sender is not Button b || b.Tag is not string tag) return;
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
            check.IsCheckedChanged += (_, _) =>
            {
                layer.Visible = check.IsChecked ?? true;
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

    private void OnAddLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null) return;
        var layer = doc.AddLayer();
        Canvas.SetActiveLayer(layer);   // fires DocumentChanged -> rebuild
        Canvas.RenderComposite();
        Canvas.InvalidateVisual();
        RebuildLayerPanel();
    }

    private void OnDeleteLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        var active = Canvas.ActiveLayer;
        if (doc is null || active is null || doc.LayerCount <= 1) return;

        int idx = doc.IndexOf(active);
        doc.RemoveLayer(active);
        active.Dispose();
        var next = doc.Layers[Math.Clamp(idx, 0, doc.LayerCount - 1)];
        Canvas.SetActiveLayer(next);
        Canvas.RenderComposite();
        Canvas.InvalidateVisual();
        RebuildLayerPanel();
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
        Canvas.RenderComposite();
        Canvas.InvalidateVisual();
        RebuildLayerPanel();
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
