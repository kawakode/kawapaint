using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using KawaPaint.Engine;

namespace KawaPaint.App;

public partial class MainView : UserControl
{
    private bool _suppress;   // guards programmatic updates to layer-panel controls
    private byte? _opacityBefore;
    private bool _dirty;
    private Layer? _dragLayer;     // row being dragged, null when no drag is in flight
    private int _dragFromIndex;    // its index at pointer-down, for a single undo entry
    private double _dragStartY;
    private bool _dragActive;      // true once the pointer moved past the click threshold

    private bool _suppressColor;      // guards programmatic updates to the color-wheel widgets
    private bool _editingSecondary;   // true while the wheel edits the background color
    private double _value = 0;        // HSV value, owned by ValueSlider
    private double _alpha = 1;        // alpha, owned by AlphaSlider

    private Palette _palette = new();
    // TODO(web): backed by a real path only on desktop (Environment.SpecialFolder isn't a real,
    // persistent filesystem under the browser sandbox) — palette/layout don't survive a page
    // reload in the browser build. Needs a localStorage-backed store to fix.
    private readonly string? _palettePath = TryGetAppDataPath("palette.kwpal");

    private UiLayout _layout = new();
    private readonly string? _layoutPath = TryGetAppDataPath("layout.json");

    // Remembers each panel's last non-Hidden placement so the top-right toggle icons can restore
    // it (rather than always falling back to the default dock side) after hiding it.
    private readonly System.Collections.Generic.Dictionary<string, string> _lastShownPlace = new()
    {
        ["Tools"] = "Left", ["Colors"] = "Bottom", ["ColorWheel"] = "Right", ["Layers"] = "Right"
    };

    // Remembers each panel's last *docked* side (never Floating/Hidden), so the per-panel float
    // button has somewhere sensible to return a floating panel to.
    private readonly System.Collections.Generic.Dictionary<string, string> _lastDockPlace = new()
    {
        ["Tools"] = "Left", ["Colors"] = "Bottom", ["ColorWheel"] = "Right", ["Layers"] = "Right"
    };

    // Floating panels are hosted as absolutely-positioned overlay windows inside FloatingLayer
    // rather than real OS windows: the browser build has no Window/popup support at all (see
    // MainWindow.axaml.cs), so an in-canvas overlay is the only approach that works everywhere.
    private readonly System.Collections.Generic.Dictionary<string, Border> _floatHosts = new();

    public static readonly int[] BrushSizePresets = { 1, 2, 3, 5, 8, 10, 15, 20, 25, 30, 40, 50, 64, 75, 100, 150, 200 };
    private const int MinBrushSize = 1;
    private const int MaxBrushSize = 500;

    public static readonly int[] TolerancePresets = { 0, 4, 8, 16, 24, 32, 48, 64, 96, 128, 160, 200, 255 };
    private const int MinTolerance = 0;
    private const int MaxTolerance = 255;

    /// <summary>The window that hosts this view, if any (null under the browser single-view host —
    /// dialogs that need a Window owner are stubbed out there; see the OwnerWindow guards below).</summary>
    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)!.StorageProvider;

    public bool IsDirty => _dirty;
    public event Action<string>? TitleChanged;

    private IStorageFile? _currentFile;   // set once a .kwp file handle is known

    private static string? TryGetAppDataPath(string fileName)
    {
        try
        {
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KawaPaint", fileName);
        }
        catch { return null; }
    }

    public MainView()
    {
        InitializeComponent();

        BlendCombo.ItemsSource = Enum.GetValues<BlendMode>();
        Canvas.DocumentChanged += (_, _) => RebuildLayerPanel();
        Canvas.PrimaryColorPicked += OnColorPicked;
        Canvas.TextRequested += OnTextRequested;
        Canvas.ZoomChanged += z => { if (ZoomText is not null) ZoomText.Text = $"{z * 100:0}%"; };
        Canvas.CursorMoved += OnCursorMoved;
        KeyDown += OnKeyDown;

        OpacitySlider.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent,
            (_, _) => _opacityBefore = Canvas.ActiveLayer?.Opacity,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        OpacitySlider.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent,
            OnOpacityCommitted, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // handledEventsToo: the ListBox marks pointer events handled for its own
        // selection handling, which would otherwise hide them from these handlers.
        LayerList.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent,
            OnLayerPointerPressed, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        LayerList.AddHandler(Avalonia.Input.InputElement.PointerMovedEvent,
            OnLayerPointerMoved, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        LayerList.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent,
            OnLayerPointerReleased, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        BuildToolPalette();
        _palette = _palettePath is null ? Palette.Default() : Palette.LoadOrDefault(_palettePath);
        BuildPaletteStrip();

        ToggleTools.Content = Icons.Create("PanelTools", 15);
        ToggleColors.Content = Icons.Create("PanelColors", 15);
        ToggleColorWheel.Content = Icons.Create("PanelColorWheel", 15);
        ToggleLayers.Content = Icons.Create("PanelLayers", 15);

        FloatToolsBtn.Content = Icons.Create("Float", 13);
        FloatColorsBtn.Content = Icons.Create("Float", 13);
        FloatColorWheelBtn.Content = Icons.Create("Float", 13);
        FloatLayersBtn.Content = Icons.Create("Float", 13);

        // handledEventsToo: ComboBox has its own built-in wheel behavior; this guarantees our step
        // logic always runs and has the final say over the box's value.
        SizeBox.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, OnSizeWheel,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ApplyBrushSize(Canvas.BrushWidth);
        ToleranceBox.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, OnToleranceWheel,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ApplyTolerance(Canvas.FillTolerance);

        _layout = _layoutPath is null ? new UiLayout() : UiLayout.LoadOrDefault(_layoutPath);
        ApplyLayout();
        SyncWheelToActiveColor();
        RefreshSwatches();
        LoadDemoDocument();
        Canvas.History.Changed += (_, _) => MarkDirty();
        SetClean(null);
        SelectTool("Pencil");
    }

    // ---- unsaved-changes tracking ----------------------------------------

    private void MarkDirty() { _dirty = true; UpdateTitle(); }

    private void SetClean(IStorageFile? file) { _dirty = false; _currentFile = file; UpdateTitle(); }

    private void UpdateTitle()
    {
        string name = _currentFile?.Name ?? "untitled";
        TitleChanged?.Invoke((_dirty ? "* " : "") + name + " — KawaPaint");
    }

    /// <summary>Returns true if it's OK to proceed (saved or discarded); false if the user cancelled.
    /// Also used by MainWindow to gate the desktop close button.</summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!_dirty) return true;
        if (OwnerWindow is not { } owner)
        {
            // TODO(web): no in-canvas confirm-discard overlay yet, so the browser build proceeds
            // without prompting — unsaved changes are silently discarded on New/Open.
            return true;
        }
        var choice = await new ConfirmSaveDialog("Save changes to the current image before continuing?")
            .ShowDialog<SaveChoice>(owner);
        return choice switch
        {
            SaveChoice.Save => await SaveProjectAsync(),
            SaveChoice.Discard => true,
            _ => false
        };
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

        int w, h;
        bool transparent;
        if (OwnerWindow is { } owner)
        {
            var dlg = new NewImageDialog();
            if (!await dlg.ShowDialog<bool>(owner)) return;
            (w, h, transparent) = (dlg.ResultWidth, dlg.ResultHeight, dlg.Transparent);
        }
        else
        {
            // TODO(web): no New-Image size dialog yet — always creates a fixed 800x600 opaque canvas.
            (w, h, transparent) = (800, 600, false);
        }

        var doc = new Document(w, h);
        var bg = doc.AddLayer("Background");
        if (!transparent) bg.Surface.Clear(ColorBgra.White);
        Canvas.SetDocument(doc);
        SetClean(null);
        StatusText.Text = $"New {w}×{h} document";
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
            await using var stream = await file.OpenReadAsync();
            using var loaded = Surface.Decode(stream);
            var doc = new Document(loaded.Width, loaded.Height);
            var layer = doc.AddLayer(System.IO.Path.GetFileNameWithoutExtension(file.Name));
            layer.Surface.CopyFrom(loaded);
            Canvas.SetDocument(doc);
            SetClean(null);   // imported image has no project file yet
            StatusText.Text = $"{file.Name} — {loaded.Width}×{loaded.Height}";
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
            Document doc;
            await using (var stream = await file.OpenReadAsync())
                doc = DocumentFile.Load(stream);
            Canvas.SetDocument(doc);
            SetClean(file);
            StatusText.Text = $"{file.Name} — {doc.LayerCount} layer(s)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Open project failed: " + ex.Message;
        }
    }

    private async void OnSaveProject(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await SaveProjectAsync();

    /// <summary>Saves the project (to the known file, or prompts). Returns true if saved.</summary>
    private async Task<bool> SaveProjectAsync()
    {
        if (Canvas.Document is null) return false;

        var file = _currentFile;
        if (file is null || !file.Name.EndsWith(DocumentFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save KawaPaint project",
                DefaultExtension = DocumentFile.Extension.TrimStart('.'),
                SuggestedFileName = "untitled" + DocumentFile.Extension
            });
            if (file is null) return false;
        }

        try
        {
            await using (var stream = await file.OpenWriteAsync())
                DocumentFile.Save(Canvas.Document, stream);
            SetClean(file);
            StatusText.Text = "Saved project " + file.Name;
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
            Title = "Export flattened image",
            DefaultExtension = "png",
            SuggestedFileName = "untitled.png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } },
                new FilePickerFileType("JPEG") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                new FilePickerFileType("WebP") { Patterns = new[] { "*.webp" } }
            }
        });
        if (file is null) return;

        try
        {
            var format = System.IO.Path.GetExtension(file.Name).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => SkiaSharp.SKEncodedImageFormat.Jpeg,
                ".webp" => SkiaSharp.SKEncodedImageFormat.Webp,
                _ => SkiaSharp.SKEncodedImageFormat.Png
            };
            using var flat = Canvas.Document.Flatten();
            await using var stream = await file.OpenWriteAsync();
            flat.Encode(stream, format, 92);
            StatusText.Text = "Exported " + file.Name;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed: " + ex.Message;
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
        Canvas.NotifyLayersChanged();
        StatusText.Text = "Applied: " + fx.Name + " (to " + layer.Name + ")";
    }

    /// <summary>
    /// Runs an operation that yields a whole new Document (crop/resize/rotate/flatten) and records
    /// it as one undo step. The displaced document stays alive in history, so these are reversible
    /// instead of silently wiping the undo stack.
    /// </summary>
    private void ApplyDocumentOp(string name, Func<Document, Document> transform)
    {
        var doc = Canvas.Document;
        if (doc is null) return;

        var replaced = Canvas.ReplaceDocument(transform(doc));
        if (replaced is null) return;
        Canvas.History.Push(new DocumentSwapMemento(name, replaced, d => Canvas.ReplaceDocument(d)));
    }

    private async void OnResize(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null) return;
        if (OwnerWindow is not { } owner)
        {
            // TODO(web): Resize needs a dialog for the target size; not available in the browser build yet.
            StatusText.Text = "Resize isn't available in the browser build yet";
            return;
        }
        var dlg = new ResizeDialog(doc.Width, doc.Height);
        if (await dlg.ShowDialog<bool>(owner))
        {
            int w = dlg.ResultWidth, h = dlg.ResultHeight;
            if (w == doc.Width && h == doc.Height) return;
            ApplyDocumentOp("Resize Image", d => DocumentOps.Resize(d, w, h));
            StatusText.Text = $"Resized to {w}×{h}";
        }
    }

    private void OnCropToSelection(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null || Canvas.Selection is not { IsActive: true } sel) { StatusText.Text = "Crop needs an active selection"; return; }

        var (x, y, w, h) = sel.GetBounds();
        if (w <= 0 || h <= 0) return;
        ApplyDocumentOp("Crop to Selection", d => DocumentOps.Crop(d, x, y, w, h));
        StatusText.Text = $"Cropped to {w}×{h}";
    }

    private void OnFlipH(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document; if (doc is null) return;
        DocumentOps.FlipHorizontal(doc);
        Canvas.History.Push(new DelegateMemento("Flip Horizontal",
            () => DocumentOps.FlipHorizontal(doc), () => DocumentOps.FlipHorizontal(doc)));
        RefreshDocument();
        StatusText.Text = "Flipped horizontally";
    }

    private void OnFlipV(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document; if (doc is null) return;
        DocumentOps.FlipVertical(doc);
        Canvas.History.Push(new DelegateMemento("Flip Vertical",
            () => DocumentOps.FlipVertical(doc), () => DocumentOps.FlipVertical(doc)));
        RefreshDocument();
        StatusText.Text = "Flipped vertically";
    }

    private void OnRotateCW(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Rotate(true);
    private void OnRotateCCW(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Rotate(false);

    private void Rotate(bool cw)
    {
        string name = cw ? "Rotate 90° CW" : "Rotate 90° CCW";
        ApplyDocumentOp(name, d => DocumentOps.Rotate90(d, cw));
        StatusText.Text = cw ? "Rotated 90° CW" : "Rotated 90° CCW";
    }

    private void OnFlatten(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null) return;
        if (doc.LayerCount <= 1) { StatusText.Text = "Already a single layer"; return; }
        ApplyDocumentOp("Flatten Image", DocumentOps.Flatten);
        StatusText.Text = "Flattened";
    }

    private void OnSelectNone(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Canvas.Selection?.SelectNone();
        Canvas.NotifySelectionChanged();
        StatusText.Text = "Selection cleared";
    }

    private void OnSelectAll(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Canvas.Selection?.SelectAll();
        Canvas.NotifySelectionChanged();
        StatusText.Text = "Selected all";
    }

    private void OnInvertSelection(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Canvas.Selection?.Invert();
        Canvas.NotifySelectionChanged();
        StatusText.Text = "Selection inverted";
    }

    private async void OnAdjust(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag || Canvas.ActiveLayer is null) return;
        if (OwnerWindow is not { } owner)
        {
            // TODO(web): live-preview Adjustment dialogs (Brightness/Contrast, Hue/Saturation,
            // Levels, Posterize, Add Noise, Gaussian Blur) need an in-canvas host; not available
            // in the browser build yet.
            StatusText.Text = "Adjustments aren't available in the browser build yet";
            return;
        }

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

        await dlg.ShowDialog(owner);
        StatusText.Text = dlg.Title ?? "Adjustment";
    }

    private async void OnCurves(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas.ActiveLayer is null) return;
        if (OwnerWindow is not { } owner)
        {
            // TODO(web): Curves needs an in-canvas dialog host; not available in the browser build yet.
            StatusText.Text = "Curves isn't available in the browser build yet";
            return;
        }
        await new CurvesDialog(Canvas).ShowDialog(owner);
        StatusText.Text = "Curves";
    }

    private void OnUndo(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.Undo();
    private void OnRedo(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.Redo();
    private void OnZoomIn(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.ZoomIn();
    private void OnZoomOut(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.ZoomOut();
    private void OnZoomFit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.ZoomToFit();
    private void OnZoomActual(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.ZoomActual();

    // ---- modular panel layout --------------------------------------------

    // Panel + its docked-to-a-side width + display title (used on the floating title bar).
    // Docked top/bottom the width is dropped so the panel spans the window instead of being
    // pinned to a thin column.
    private sealed record PanelSpec(Border Border, string Key, string Title, double SideWidth);

    private PanelSpec[] Panels => new[]
    {
        new PanelSpec(ToolsBorder, "Tools", "Tools", 70.0),
        new PanelSpec(ColorsBorder, "Colors", "Colors", double.NaN),
        new PanelSpec(ColorWheelBorder, "ColorWheel", "Color", 190.0),
        new PanelSpec(LayersBorder, "Layers", "Layers", 220.0)
    };

    private void ApplyLayout()
    {
        var panels = Panels;

        // Detach every panel Border from wherever it currently lives — RootDock, or a floating
        // host's content slot — before re-placing it, since a control can only have one parent.
        foreach (var p in panels)
        {
            RootDock.Children.Remove(p.Border);
            if (_floatHosts.TryGetValue(p.Key, out var host) && host.Child is DockPanel hostDock)
                hostDock.Children.Remove(p.Border);
        }
        RootDock.Children.Remove(Canvas);
        foreach (var host in _floatHosts.Values) FloatingLayer.Children.Remove(host);

        foreach (var p in panels)
        {
            string place = _layout.GetPlace(p.Key);

            if (place == "Hidden") { p.Border.IsVisible = false; continue; }
            p.Border.IsVisible = true;

            if (place == "Floating")
            {
                var host = GetOrCreateFloatHost(p);
                var (x, y) = _layout.GetFloatPos(p.Key);
                host.Margin = new Thickness(Math.Max(0, x), Math.Max(0, y), 0, 0);
                FloatingLayer.Children.Add(host);
                continue;
            }

            var dock = ParseDock(place);
            DockPanel.SetDock(p.Border, dock);
            p.Border.Width = dock is Dock.Left or Dock.Right ? p.SideWidth : double.NaN;
            RootDock.Children.Add(p.Border);
        }
        RootDock.Children.Add(Canvas);   // fill

        RefreshPanelToggleButtons();
    }

    private static Dock ParseDock(string s) => s switch
    {
        "Left" => Dock.Left,
        "Right" => Dock.Right,
        "Top" => Dock.Top,
        _ => Dock.Bottom
    };

    private void OnPanelPlace(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string? tag = null;
        if (sender is Button b) tag = b.Tag as string;
        else if (sender is MenuItem m) tag = m.Tag as string;
        if (tag is null) return;

        var parts = tag.Split(':');
        if (parts.Length != 2) return;
        string key = parts[0], place = parts[1];
        if (place != "Hidden") _lastShownPlace[key] = place;
        if (place is "Left" or "Right" or "Top" or "Bottom") _lastDockPlace[key] = place;
        _layout.SetPlace(key, place);
        ApplyLayout();
        PersistLayout();
    }

    /// <summary>Top-right icon toggle: hides a visible panel, or restores a hidden one to
    /// wherever it was last shown (its dock side, or Floating at its last position).</summary>
    private void OnPanelToggle(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not string key) return;

        string place = _layout.GetPlace(key);
        if (place == "Hidden")
            _layout.SetPlace(key, _lastShownPlace.TryGetValue(key, out var last) ? last : "Left");
        else
        {
            _lastShownPlace[key] = place;
            _layout.SetPlace(key, "Hidden");
        }
        ApplyLayout();
        PersistLayout();
    }

    /// <summary>Per-panel float button: undocks a docked panel to Floating, or sends a floating
    /// panel back to wherever it was last docked.</summary>
    private void OnPanelFloatToggle(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not string key) return;

        string place = _layout.GetPlace(key);
        string next = place == "Floating"
            ? (_lastDockPlace.TryGetValue(key, out var dock) ? dock : "Left")
            : "Floating";
        _lastShownPlace[key] = next;
        _layout.SetPlace(key, next);
        ApplyLayout();
        PersistLayout();
    }

    private void RefreshPanelToggleButtons()
    {
        ToggleTools.IsChecked = _layout.GetPlace("Tools") != "Hidden";
        ToggleColors.IsChecked = _layout.GetPlace("Colors") != "Hidden";
        ToggleColorWheel.IsChecked = _layout.GetPlace("ColorWheel") != "Hidden";
        ToggleLayers.IsChecked = _layout.GetPlace("Layers") != "Hidden";

        RefreshFloatButton(FloatToolsBtn, "Tools");
        RefreshFloatButton(FloatColorsBtn, "Colors");
        RefreshFloatButton(FloatColorWheelBtn, "ColorWheel");
        RefreshFloatButton(FloatLayersBtn, "Layers");
    }

    private void RefreshFloatButton(ToggleButton btn, string key)
    {
        bool floating = _layout.GetPlace(key) == "Floating";
        btn.IsChecked = floating;
        ToolTip.SetTip(btn, floating ? "Dock this panel" : "Float this panel");
    }

    private void OnResetLayout(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _layout = new UiLayout();
        ApplyLayout();
        PersistLayout();
    }

    private void PersistLayout()
    {
        if (_layoutPath is null) return;
        try { _layout.Save(_layoutPath); } catch { /* ignore */ }
    }

    // ---- floating panels ---------------------------------------------------
    //
    // A floating panel is the same Border used when docked, reparented into a small window-like
    // frame (title bar + close button) absolutely positioned within FloatingLayer via Margin.
    // Hosts are created once and cached, so drag handlers are wired exactly once per panel.

    private Border GetOrCreateFloatHost(PanelSpec spec)
    {
        if (_floatHosts.TryGetValue(spec.Key, out var existing))
        {
            // ApplyLayout unconditionally detaches spec.Border from the host's DockPanel before
            // re-placing every panel (see the top of ApplyLayout), so a reused host always needs
            // its content put back — otherwise a second float placement leaves only the title bar.
            if (existing.Child is DockPanel existingDock && !existingDock.Children.Contains(spec.Border))
                existingDock.Children.Add(spec.Border);
            return existing;
        }

        spec.Border.Width = spec.SideWidth;

        var titleText = new TextBlock
        {
            Text = spec.Title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var closeBtn = new Button { Content = "✕", Padding = new Thickness(6, 0) };
        ToolTip.SetTip(closeBtn, "Hide (View ▸ Panels to restore)");
        closeBtn.Click += (_, _) =>
        {
            _layout.SetPlace(spec.Key, "Hidden");
            ApplyLayout();
            PersistLayout();
        };

        var titleGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleGrid.Children.Add(titleText);
        Grid.SetColumn(closeBtn, 1);
        titleGrid.Children.Add(closeBtn);

        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Padding = new Thickness(8, 4),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeAll),
            Child = titleGrid
        };
        DockPanel.SetDock(titleBar, Dock.Top);

        var hostDock = new DockPanel { LastChildFill = true };
        hostDock.Children.Add(titleBar);
        hostDock.Children.Add(spec.Border);

        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 4 16 0 #A0000000"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = hostDock
        };

        WireFloatDrag(titleBar, host, spec.Key);
        host.PointerPressed += (_, _) => BringToFront(host);

        _floatHosts[spec.Key] = host;
        return host;
    }

    private void WireFloatDrag(Border titleBar, Border host, string key)
    {
        Point dragStart = default;
        Point marginStart = default;
        bool dragging = false;

        titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed) return;
            // Reordering FloatingLayer.Children detaches/reattaches host's visual subtree, which
            // silently drops pointer capture — so this must happen *before* Capture is requested,
            // never after.
            BringToFront(host);
            dragging = true;
            dragStart = e.GetPosition(FloatingLayer);
            marginStart = new Point(host.Margin.Left, host.Margin.Top);
            e.Pointer.Capture(titleBar);
        };
        titleBar.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var p = e.GetPosition(FloatingLayer);
            double nx = Math.Max(0, marginStart.X + (p.X - dragStart.X));
            double ny = Math.Max(0, marginStart.Y + (p.Y - dragStart.Y));
            host.Margin = new Thickness(nx, ny, 0, 0);
        };
        titleBar.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            _layout.SetFloatPos(key, host.Margin.Left, host.Margin.Top);
            PersistLayout();
        };
    }

    private void BringToFront(Border host)
    {
        // No-op when already topmost: reordering detaches/reattaches host's visual subtree, which
        // would drop pointer capture if this ran again mid-drag (see WireFloatDrag).
        if (FloatingLayer.Children.Count > 0 && ReferenceEquals(FloatingLayer.Children[^1], host)) return;
        FloatingLayer.Children.Remove(host);
        FloatingLayer.Children.Add(host);
    }

    // No-op under the browser single-view host (no desktop window to close).
    private void OnExit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OwnerWindow?.Close();

    // ---- toolbar ----------------------------------------------------------

    private void OnColor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hex) return;
        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte bl = Convert.ToByte(hex.Substring(5, 2), 16);
        // These swatches are labelled "Fg", so they always set the foreground — regardless of
        // which swatch the color wheel currently edits.
        SetForeground(ColorBgra.FromBgra(bl, g, r, 255));
    }

    // ---- color wheel panel ------------------------------------------------
    //
    // The wheel edits whichever swatch is active (foreground by default). The three
    // input widgets each own one part of the color: the ring gives hue+saturation,
    // and the two sliders give value and alpha. They are recombined here rather than
    // cross-bound, so a change from any one of them can't feed back into the others.

    /// <summary>Reads the panel widgets back into a single color and applies it.</summary>
    private void CommitWheelColor()
    {
        if (_suppressColor) return;
        var hsv = ColorWheel.HsvColor;
        var c = new HsvColor(_alpha, hsv.H, hsv.S, _value).ToRgb();
        SetActiveColor(c);
    }

    private void OnSpectrumChanged(object? sender, Avalonia.Controls.ColorChangedEventArgs e) => CommitWheelColor();

    private void OnValueSliderChanged(object? sender, Avalonia.Controls.ColorChangedEventArgs e)
    {
        if (_suppressColor) return;
        _value = ValueSlider.HsvColor.V;
        CommitWheelColor();
    }

    private void OnAlphaSliderChanged(object? sender, Avalonia.Controls.ColorChangedEventArgs e)
    {
        if (_suppressColor) return;
        _alpha = AlphaSlider.HsvColor.A;
        CommitWheelColor();
    }

    private void OnSelectFg(object? sender, Avalonia.Input.PointerPressedEventArgs e) => SetEditTarget(secondary: false);

    private void OnSelectBg(object? sender, Avalonia.Input.PointerPressedEventArgs e) => SetEditTarget(secondary: true);

    private void SetEditTarget(bool secondary)
    {
        _editingSecondary = secondary;
        SyncWheelToActiveColor();
        RefreshSwatches();
    }

    private void OnSwapColors(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        (Canvas.BrushColor, Canvas.SecondaryColor) = (Canvas.SecondaryColor, Canvas.BrushColor);
        SyncWheelToActiveColor();
        RefreshSwatches();
    }

    private void OnHexKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        OnHexCommit(sender, e);
        e.Handled = true;
    }

    private void OnHexCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = (HexBox.Text ?? "").Trim().TrimStart('#');
        if ((text.Length == 6 || text.Length == 8) &&
            uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint v))
        {
            byte a = text.Length == 8 ? (byte)(v >> 24) : (byte)255;
            SetActiveColor(Color.FromArgb(a, (byte)(v >> 16), (byte)(v >> 8), (byte)v));
            SyncWheelToActiveColor();
        }
        RefreshSwatches();   // rewrites the box from the real color, reverting bad input
    }

    /// <summary>Applies a color to the active target and refreshes the panel readouts.</summary>
    private void SetActiveColor(Color c)
    {
        if (Canvas is null) return;
        var bgra = ColorBgra.FromBgra(c.B, c.G, c.R, c.A);
        if (_editingSecondary) Canvas.SecondaryColor = bgra;
        else Canvas.BrushColor = bgra;
        RefreshSwatches();
    }

    /// <summary>Pushes the active color back into the wheel/sliders without re-triggering them.</summary>
    private void SyncWheelToActiveColor()
    {
        var bgra = _editingSecondary ? Canvas.SecondaryColor : Canvas.BrushColor;
        var c = Color.FromArgb(bgra.A, bgra.R, bgra.G, bgra.B);
        var hsv = c.ToHsv();

        _suppressColor = true;
        _value = hsv.V;
        _alpha = hsv.A;
        ColorWheel.HsvColor = hsv;
        ValueSlider.HsvColor = hsv;
        AlphaSlider.HsvColor = hsv;
        _suppressColor = false;
    }

    /// <summary>Repaints the Fg/Bg swatches, the active-target outline, and the hex box.</summary>
    private void RefreshSwatches()
    {
        if (FgSwatch is null || Canvas is null) return;

        var fg = Canvas.BrushColor;
        var bg = Canvas.SecondaryColor;
        FgSwatch.Background = new SolidColorBrush(Color.FromArgb(fg.A, fg.R, fg.G, fg.B));
        BgSwatch.Background = new SolidColorBrush(Color.FromArgb(bg.A, bg.R, bg.G, bg.B));

        var active = new SolidColorBrush(Color.FromRgb(0x8C, 0xB4, 0xFF));
        var idle = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        FgSwatch.BorderBrush = _editingSecondary ? idle : active;
        BgSwatch.BorderBrush = _editingSecondary ? active : idle;

        var cur = _editingSecondary ? bg : fg;
        HexBox.Text = cur.A == 255
            ? $"{cur.R:X2}{cur.G:X2}{cur.B:X2}"
            : $"{cur.A:X2}{cur.R:X2}{cur.G:X2}{cur.B:X2}";
    }

    // ---- color palette ----------------------------------------------------

    private void BuildPaletteStrip()
    {
        PaletteStrip.Children.Clear();
        foreach (var entry in _palette.Colors)
        {
            var e = entry;
            var color = e.Color;
            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(1),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)),
                Classes = { "swatch" },
                Tag = e
            };
            ToolTip.SetTip(swatch, string.IsNullOrEmpty(e.Name) ? color.ToHexString() : $"{e.Name}  ({color.ToHexString()})");
            swatch.Click += (_, _) => SetForeground(color);

            var menu = new ContextMenu();
            var asBg = new MenuItem { Header = "Set as Background" };
            asBg.Click += (_, _) => SetBackground(color);
            var rename = new MenuItem { Header = "Rename…" };
            rename.Click += async (_, _) =>
            {
                if (OwnerWindow is not { } owner)
                {
                    // TODO(web): palette color rename needs an in-canvas prompt; not available
                    // in the browser build yet.
                    StatusText.Text = "Rename isn't available in the browser build yet";
                    return;
                }
                var dlg = new PromptDialog("Name color", e.Name ?? "");
                if (await dlg.ShowDialog<bool>(owner)) { e.Name = dlg.ResultText.Trim(); PersistPalette(); BuildPaletteStrip(); }
            };
            var remove = new MenuItem { Header = "Remove" };
            remove.Click += (_, _) => { _palette.Colors.Remove(e); PersistPalette(); BuildPaletteStrip(); };
            menu.Items.Add(asBg);
            menu.Items.Add(rename);
            menu.Items.Add(remove);
            swatch.ContextMenu = menu;

            PaletteStrip.Children.Add(swatch);
        }
    }

    private void SetForeground(ColorBgra c)
    {
        Canvas.BrushColor = c;
        if (!_editingSecondary) SyncWheelToActiveColor();
        RefreshSwatches();
    }

    private void SetBackground(ColorBgra c)
    {
        Canvas.SecondaryColor = c;
        if (_editingSecondary) SyncWheelToActiveColor();
        RefreshSwatches();
    }

    private void PersistPalette()
    {
        if (_palettePath is null) return;
        try { _palette.Save(_palettePath); } catch { /* ignore */ }
    }

    private void OnAddPaletteColor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _palette.Add(Canvas.BrushColor);
        PersistPalette();
        BuildPaletteStrip();
        StatusText.Text = "Added color to palette";
    }

    private async void OnSavePalette(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save palette",
            DefaultExtension = "kwpal",
            SuggestedFileName = "palette.kwpal"
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            _palette.Save(stream);
            StatusText.Text = "Palette saved";
        }
        catch (Exception ex) { StatusText.Text = "Save palette failed: " + ex.Message; }
    }

    private async void OnLoadPalette(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load palette",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("KawaPaint palette") { Patterns = new[] { "*.kwpal" } } }
        });
        var file = files.FirstOrDefault();
        if (file is null) return;
        await using (var stream = await file.OpenReadAsync())
            _palette = Palette.LoadOrDefault(stream);
        PersistPalette();
        BuildPaletteStrip();
        StatusText.Text = "Palette loaded";
    }

    private void OnTool(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) SelectTool(tag);
    }

    private static readonly (string Key, string Name, string Shortcut)[] ToolDefs =
    {
        ("Pencil", "Pencil", "P"), ("Eraser", "Eraser", "E"), ("Fill", "Paint Bucket", "F"),
        ("Pick", "Color Picker", "K"), ("Line", "Line", "L"), ("Rect", "Rectangle", "R"),
        ("Ellipse", "Ellipse", "O"), ("Gradient", "Gradient", "G"), ("Text", "Text", "T"),
        ("Move", "Move", "M"), ("RectSel", "Rectangle Select", "S"),
        ("EllipseSel", "Ellipse Select", "S S"), ("Lasso", "Lasso Select", "S S S")
    };

    private readonly System.Collections.Generic.List<ToggleButton> _toolButtons = new();
    private string _currentToolTag = "Pencil";

    private void BuildToolPalette()
    {
        foreach (var (key, name, sc) in ToolDefs)
        {
            var btn = new ToggleButton
            {
                Content = Icons.Create(key),
                Width = 28,
                Height = 28,
                Padding = new Thickness(3),
                Margin = new Thickness(1),
                Tag = key
            };
            ToolTip.SetTip(btn, string.IsNullOrEmpty(sc) ? name : $"{name}   ({sc})");
            btn.Click += (_, _) => SelectTool(key);
            _toolButtons.Add(btn);
            ToolPalette.Children.Add(btn);
        }
    }

    private void SelectTool(string tag)
    {
        _currentToolTag = tag;
        foreach (var b in _toolButtons)
            b.IsChecked = (b.Tag as string) == tag;

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
        UpdateToolOptions(tag);
        StatusText.Text = "Tool: " + tool.Name;
    }

    /// <summary>Greys out toolbar options the active tool ignores.</summary>
    private void UpdateToolOptions(string tag)
    {
        SizeGroup.IsEnabled = tag is "Pencil" or "Eraser" or "Line" or "Rect" or "Ellipse";
        ShapeGroup.IsEnabled = tag is "Pencil" or "Line" or "Rect" or "Ellipse";
        FillShapesCheck.IsEnabled = tag is "Rect" or "Ellipse";
        BucketGroup.IsEnabled = tag == "Fill";
    }

    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        var empty = new Avalonia.Interactivity.RoutedEventArgs();
        // Ctrl, optionally with Shift — and nothing else, so AltGr (= Ctrl+Alt on many layouts)
        // does not fire menu commands while typing.
        bool ctrl = (e.KeyModifiers & ~Avalonia.Input.KeyModifiers.Shift) == Avalonia.Input.KeyModifiers.Control;
        bool shift = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
        bool inTextBox = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

        // Avalonia's MenuItem.InputGesture only *renders* the shortcut text — it never handles the
        // key — so every menu accelerator has to be dispatched here or it does nothing.
        if (ctrl)
        {
            switch (e.Key)
            {
                // Editing shortcuts stay with a focused text field (undo/select-all in the box).
                case Avalonia.Input.Key.Z when !inTextBox:
                    if (shift) Canvas.Redo(); else Canvas.Undo(); break;
                case Avalonia.Input.Key.Y when !inTextBox: Canvas.Redo(); break;
                case Avalonia.Input.Key.A when !inTextBox: OnSelectAll(sender, empty); break;

                case Avalonia.Input.Key.I: OnInvertSelection(sender, empty); break;
                case Avalonia.Input.Key.D: OnSelectNone(sender, empty); break;

                case Avalonia.Input.Key.N: OnNew(sender, empty); break;
                case Avalonia.Input.Key.O:
                    if (shift) OnOpenProject(sender, empty); else OnOpen(sender, empty); break;
                case Avalonia.Input.Key.S:
                    if (shift) OnSaveAs(sender, empty); else OnSaveProject(sender, empty); break;

                case Avalonia.Input.Key.OemPlus:
                case Avalonia.Input.Key.Add: Canvas.ZoomIn(); break;
                case Avalonia.Input.Key.OemMinus:
                case Avalonia.Input.Key.Subtract: Canvas.ZoomOut(); break;
                case Avalonia.Input.Key.D0: Canvas.ZoomToFit(); break;
                case Avalonia.Input.Key.D1: Canvas.ZoomActual(); break;

                default: return;
            }
            e.Handled = true;
            return;
        }

        // Ignore when typing into a control (e.g. a text field gets focus).
        if (e.KeyModifiers != Avalonia.Input.KeyModifiers.None || inTextBox) return;

        // The three selection tools share one key and cycle through it.
        if (e.Key == Avalonia.Input.Key.S)
        {
            SelectTool(_currentToolTag switch
            {
                "RectSel" => "EllipseSel",
                "EllipseSel" => "Lasso",
                _ => "RectSel"
            });
            e.Handled = true;
            return;
        }

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
            _ => null
        };
        if (tag is not null) { SelectTool(tag); e.Handled = true; }
    }

    private async void OnTextRequested(int x, int y)
    {
        var layer = Canvas.ActiveLayer;
        if (layer is null) return;
        if (OwnerWindow is not { } owner)
        {
            // TODO(web): Text tool needs an in-canvas prompt for the string/size; not available
            // in the browser build yet.
            StatusText.Text = "Text tool isn't available in the browser build yet";
            return;
        }

        var dlg = new TextDialog();
        bool ok = await dlg.ShowDialog<bool>(owner);
        if (!ok || string.IsNullOrEmpty(dlg.ResultText)) return;

        var snapshot = layer.Surface.Clone();
        TextOps.DrawText(layer.Surface, dlg.ResultText, x, y, dlg.ResultSize, Canvas.BrushColor);
        if (Canvas.Selection is { IsActive: true }) Canvas.Selection.Clip(layer.Surface, snapshot);
        Canvas.History.Push(LayerSurfaceMemento.FromSnapshot(layer, snapshot, "Text"));
        Canvas.RenderComposite();
        Canvas.InvalidateVisual();
        Canvas.NotifyLayersChanged();
        StatusText.Text = "Added text";
    }

    private void OnColorPicked(ColorBgra c)
    {
        // The eyedropper always targets the foreground; SurfaceView has already applied it.
        if (!_editingSecondary) SyncWheelToActiveColor();
        RefreshSwatches();
        StatusText.Text = $"Picked {c}";
    }

    /// <summary>Status-bar readout; blank while the pointer is off the canvas.</summary>
    private void OnCursorMoved(int x, int y)
    {
        if (CoordText is null) return;
        var doc = Canvas.Document;
        bool inside = doc is not null && (uint)x < (uint)doc.Width && (uint)y < (uint)doc.Height;
        CoordText.Text = inside ? $"{x}, {y}" : "";
    }

    private bool _suppressSize;   // guards programmatic updates to SizeBox while applying a size

    /// <summary>Sets the brush/outline size, clamped to range, and syncs SizeBox to match —
    /// selecting the matching preset if there is one, and always updating the editable text.</summary>
    private void ApplyBrushSize(int size)
    {
        size = Math.Clamp(size, MinBrushSize, MaxBrushSize);
        if (Canvas is not null) Canvas.BrushWidth = size;
        if (SizeBox is null) return;

        _suppressSize = true;
        SizeBox.SelectedItem = Array.IndexOf(BrushSizePresets, size) >= 0 ? size : null;
        SizeBox.Text = size.ToString();
        _suppressSize = false;
    }

    private void OnSizePresetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSize) return;
        if (SizeBox.SelectedItem is int size) ApplyBrushSize(size);
    }

    private void OnSizeKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        CommitSizeText();
        e.Handled = true;
    }

    private void OnSizeTextCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => CommitSizeText();

    /// <summary>Parses whatever the user typed into the editable box; reverts to the current size
    /// on unparsable input instead of leaving the box showing something that was never applied.</summary>
    private void CommitSizeText()
    {
        if (_suppressSize || SizeBox is null) return;
        ApplyBrushSize(int.TryParse((SizeBox.Text ?? "").Trim(), out int size) ? size : Canvas.BrushWidth);
    }

    /// <summary>Lets the wheel nudge the size while hovering SizeBox, Shift for a bigger jump —
    /// independent of the box's own built-in wheel behavior (see the handledEventsToo hookup).</summary>
    private void OnSizeWheel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        int step = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? 10 : 1;
        int delta = Math.Sign(e.Delta.Y) * step;
        if (delta == 0) return;
        ApplyBrushSize(Canvas.BrushWidth + delta);
        e.Handled = true;
    }

    private void OnAntialias(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas is not null && AntialiasCheck is not null) Canvas.Antialias = AntialiasCheck.IsChecked ?? true;
    }

    private void OnFillShapes(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas is not null && FillShapesCheck is not null) Canvas.FillShapes = FillShapesCheck.IsChecked ?? false;
    }

    private void OnGlobalFill(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas is not null && GlobalFillCheck is not null) Canvas.GlobalFill = GlobalFillCheck.IsChecked ?? false;
    }

    private bool _suppressTolerance;   // guards programmatic updates to ToleranceBox

    /// <summary>Sets the fill tolerance, clamped to range, and syncs ToleranceBox to match —
    /// selecting the matching preset if there is one, and always updating the editable text.</summary>
    private void ApplyTolerance(int tol)
    {
        tol = Math.Clamp(tol, MinTolerance, MaxTolerance);
        if (Canvas is not null) Canvas.FillTolerance = tol;
        if (ToleranceBox is null) return;

        _suppressTolerance = true;
        ToleranceBox.SelectedItem = Array.IndexOf(TolerancePresets, tol) >= 0 ? tol : null;
        ToleranceBox.Text = tol.ToString();
        _suppressTolerance = false;
    }

    private void OnTolerancePresetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTolerance) return;
        if (ToleranceBox.SelectedItem is int tol) ApplyTolerance(tol);
    }

    private void OnToleranceKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        CommitToleranceText();
        e.Handled = true;
    }

    private void OnToleranceTextCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => CommitToleranceText();

    private void CommitToleranceText()
    {
        if (_suppressTolerance || ToleranceBox is null) return;
        ApplyTolerance(int.TryParse((ToleranceBox.Text ?? "").Trim(), out int tol) ? tol : Canvas.FillTolerance);
    }

    private void OnToleranceWheel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        int step = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? 10 : 1;
        int delta = Math.Sign(e.Delta.Y) * step;
        if (delta == 0) return;
        ApplyTolerance(Canvas.FillTolerance + delta);
        e.Handled = true;
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
            var thumb = new Border
            {
                Width = 40, Height = 30, Background = Brushes.DimGray,
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Image { Source = MakeThumbnail(layer.Surface, 38, 28), Stretch = Stretch.Uniform }
            };
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
            panel.Children.Add(thumb);
            panel.Children.Add(new TextBlock { Text = layer.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White });

            var item = new ListBoxItem { Content = panel, Tag = layer };
            item.DoubleTapped += async (_, _) =>
            {
                if (OwnerWindow is not { } owner)
                {
                    // TODO(web): layer rename needs an in-canvas prompt; not available in the
                    // browser build yet.
                    StatusText.Text = "Layer rename isn't available in the browser build yet";
                    return;
                }
                var dlg = new PromptDialog("Rename layer", capturedLayer.Name);
                if (await dlg.ShowDialog<bool>(owner) && !string.IsNullOrWhiteSpace(dlg.ResultText))
                {
                    capturedLayer.Name = dlg.ResultText.Trim();
                    MarkDirty();
                    RebuildLayerPanel();
                }
            };

            LayerList.Items.Add(item);
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

    private static unsafe WriteableBitmap MakeThumbnail(Surface s, int maxW, int maxH)
    {
        double scale = Math.Min((double)maxW / s.Width, (double)maxH / s.Height);
        int tw = Math.Max(1, (int)(s.Width * scale));
        int th = Math.Max(1, (int)(s.Height * scale));
        using var small = s.Resized(tw, th);
        var wb = new WriteableBitmap(new PixelSize(tw, th), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var fb = wb.Lock())
        {
            int rowBytes = tw * 4;
            byte* dst = (byte*)fb.Address;
            for (int y = 0; y < th; y++)
                System.Buffer.MemoryCopy(small.GetRowPointer(y), dst + (long)y * fb.RowBytes, fb.RowBytes, rowBytes);
        }
        return wb;
    }

    private void OnLayerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (LayerList.SelectedItem is ListBoxItem { Tag: Layer layer })
            Canvas.SetActiveLayer(layer);
    }

    // ---- layer drag-reorder ----------------------------------------------
    //
    // These handlers sit on the ListBox rather than on each row. Reordering rebuilds
    // every row, which would destroy the control a per-row gesture started on and
    // strand the drag half-finished; the list itself survives. The rows are reordered
    // live as the pointer passes over them, but history is deferred to pointer-up so
    // that dragging across several positions stays a single undo step.

    private void OnLayerPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(LayerList).Properties.IsLeftButtonPressed) return;
        if (Canvas.Document is not { } doc) return;
        if (RowAt(e.GetPosition(LayerList).Y)?.Tag is not Layer layer) return;

        _dragLayer = layer;
        _dragFromIndex = doc.IndexOf(layer);
        _dragStartY = e.GetPosition(LayerList).Y;
        _dragActive = false;
    }

    private void OnLayerPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_dragLayer is null || Canvas.Document is not { } doc) return;

        double y = e.GetPosition(LayerList).Y;
        if (!_dragActive)
        {
            if (Math.Abs(y - _dragStartY) < 4) return;   // let a plain click through untouched
            _dragActive = true;
            // Captured only once it is really a drag, so click and double-tap-to-rename
            // keep reaching the row itself.
            e.Pointer.Capture(LayerList);
        }

        if (RowAt(y)?.Tag is not Layer over || ReferenceEquals(over, _dragLayer)) return;

        int from = doc.IndexOf(_dragLayer);
        int to = doc.IndexOf(over);
        if (from < 0 || to < 0) return;

        doc.MoveLayer(from, to);
        RefreshDocument();
    }

    private void OnLayerPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        var layer = _dragLayer;
        bool dragged = _dragActive;
        _dragLayer = null;
        _dragActive = false;
        if (dragged) e.Pointer.Capture(null);

        if (!dragged || layer is null || Canvas.Document is not { } doc) return;

        int from = _dragFromIndex, to = doc.IndexOf(layer);
        if (to < 0 || to == from) return;

        Canvas.History.Push(new DelegateMemento("Reorder Layer",
            undo: () => doc.MoveLayer(to, from),
            redo: () => doc.MoveLayer(from, to)));
    }

    /// <summary>Finds the layer row containing <paramref name="y"/>, in ListBox coordinates.</summary>
    private ListBoxItem? RowAt(double y)
    {
        foreach (ListBoxItem row in LayerList.Items.Cast<ListBoxItem>())
        {
            double? top = row.TranslatePoint(default, LayerList)?.Y;
            if (top is not null && y >= top && y < top + row.Bounds.Height) return row;
        }
        return null;
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
            var layer = Canvas.ActiveLayer;
            BlendMode old = e.RemovedItems.Count > 0 && e.RemovedItems[0] is BlendMode om ? om : layer.BlendMode;
            layer.BlendMode = mode;
            Canvas.History.Push(new DelegateMemento("Blend Mode",
                () => layer.BlendMode = old, () => layer.BlendMode = mode));
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

    private void OnOpacityCommitted(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        var layer = Canvas.ActiveLayer;
        if (layer is null || _opacityBefore is null) return;
        byte before = _opacityBefore.Value, after = layer.Opacity;
        _opacityBefore = null;
        if (before != after)
            Canvas.History.Push(new DelegateMemento("Opacity",
                () => layer.Opacity = before, () => layer.Opacity = after));
    }
}
