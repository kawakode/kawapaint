using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KawaPaint.App.Core;
using KawaPaint.Engine;
using KawaPaint.Engine.Codecs;
using KawaPaint.Engine.Metadata;

namespace KawaPaint.App;

public partial class MainView : UserControl
{
    private bool _suppress;   // guards programmatic updates to layer-panel controls
    private bool _suppressHistory;   // guards programmatic updates to the History panel's selection
    private byte? _opacityBefore;
    private Layer? _opacityLayer;   // the layer that edit started on; see CommitOpacityChange
    private Layer? _dragLayer;     // row being dragged, null when no drag is in flight
    private int _dragFromIndex;    // its index at pointer-down, for a single undo entry
    private double _dragStartY;
    private bool _dragActive;      // true once the pointer moved past the click threshold

    private bool _suppressColor;      // guards programmatic updates to the color-wheel widgets
    private bool _editingSecondary;   // true while the wheel edits the background color
    private double _value = 0;        // HSV value, owned by ValueSlider
    private double _alpha = 1;        // alpha, owned by AlphaSlider
    private SolidColorBrush? _valueCursorBrush;   // fills the Value slider's thumb; see ValueSliderCursorBrush

    private Palette _palette = new();

    // Under the app directory rather than a path of its own, so enabling git tracking later
    // covers the palette along with everything else. Null on the browser build (no filesystem).
    private readonly string? _palettePath =
        AppPaths.Root is null ? null : System.IO.Path.Combine(AppPaths.Root, "palette.kwpal");

    private readonly SettingsService _settings = SettingsService.Instance;
    private PanelManager _panels = null!;   // built in the constructor, once the AXAML tree exists
    private readonly CommandRegistry _commands = new();
    private AutosaveService? _autosave;
    private ConfigGitTracker? _configGit;

    public static readonly int[] BrushSizePresets = { 1, 2, 3, 5, 8, 10, 15, 20, 25, 30, 40, 50, 64, 75, 100, 150, 200 };
    private const int MinBrushSize = 1;
    private const int MaxBrushSize = 500;

    /// <summary>Paintbrush edge hardness, as whole percent - the toolbar talks in percent while
    /// SurfaceView.BrushHardness (and the engine below it) works in 0..1.</summary>
    public static readonly int[] HardnessPresets = { 0, 10, 25, 50, 75, 90, 100 };
    private const int MinHardness = 0;
    private const int MaxHardness = 100;

    public static readonly int[] TolerancePresets = { 0, 4, 8, 16, 24, 32, 48, 64, 96, 128, 160, 200, 255 };
    private const int MinTolerance = 0;
    private const int MaxTolerance = 255;

    /// <summary>The window that hosts this view, if any (null under the browser single-view host -
    /// dialogs that need a Window owner are stubbed out there; see the OwnerWindow guards below).</summary>
    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)!.StorageProvider;

    public bool IsDirty => _session?.IsDirty ?? false;
    public event Action<string>? TitleChanged;

    /// <summary>
    /// Save state for the open document: path, dirty flag and edit counter. Autosave, crash
    /// recovery and git tracking all read from here rather than keeping their own flags.
    /// </summary>
    private DocumentSession? _session;

    private IStorageFile? _currentFile;   // set once a .kwp file handle is known

    public MainView()
    {
        InitializeComponent();

        _valueCursorBrush = this.Resources["ValueSliderCursorBrush"] as SolidColorBrush;

        BlendCombo.ItemsSource = Enum.GetValues<BlendMode>();
        // Posted rather than called inline: DocumentChanged can fire from inside LayerList's own
        // SelectionChanged dispatch (a row click -> OnLayerSelected -> SetActiveLayer ->
        // DocumentChanged), and RebuildLayerPanel's Items.Clear() reentering that same dispatch
        // crashes Avalonia's SelectionModel (ArgumentOutOfRangeException deep in its internals).
        // Posting lets the click's own dispatch finish first.
        Canvas.DocumentChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RebuildLayerPanel();
            RebuildTimeline();
        });
        Canvas.PrimaryColorPicked += OnColorPicked;
        Canvas.TextRequested += OnTextRequested;
        Canvas.DynamicTextRequested += OnDynamicTextRequested;
        Canvas.ZoomChanged += z => { if (ZoomText is not null) ZoomText.Text = $"{z * 100:0}%"; };
        Canvas.CursorMoved += OnCursorMoved;
        KeyDown += OnKeyDown;

        // "Before" is captured lazily in OnOpacityChanged itself (the first change of a gesture,
        // whichever input drove it), so a mouse drag and an arrow-key nudge both get a correct
        // undo baseline. Commit fires on any gesture-end signal: PointerReleased for a drag,
        // KeyUp/LostFocus for keyboard (arrow keys don't raise pointer events at all). KeyUp uses
        // handledEventsToo/Bubble since Slider's arrow-key handling lives on its internal template
        // part, not necessarily the OpacitySlider element itself.
        OpacitySlider.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent,
            OnOpacityCommitted, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        OpacitySlider.AddHandler(Avalonia.Input.InputElement.KeyUpEvent,
            (_, _) => CommitOpacityChange(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        OpacitySlider.LostFocus += (_, _) => CommitOpacityChange();

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
        ToggleHistory.Content = Icons.Create("PanelHistory", 15);
        ToggleTimeline.Content = Icons.Create("PanelTimeline", 15);
        ToggleDock.Content = Icons.Create("PanelDock", 15);

        FloatToolsBtn.Content = Icons.Create("Float", 13);
        FloatColorsBtn.Content = Icons.Create("Float", 13);
        FloatColorWheelBtn.Content = Icons.Create("Float", 13);
        FloatLayersBtn.Content = Icons.Create("Float", 13);
        FloatHistoryBtn.Content = Icons.Create("Float", 13);
        FloatTimelineBtn.Content = Icons.Create("Float", 13);
        FloatDockBtn.Content = Icons.Create("Float", 13);

        _suppressFrames = true;
        ShowFramePreviewsCheck.IsChecked = _settings.Settings.Workspace.ShowFramePreviews;
        _suppressFrames = false;

        // handledEventsToo: ComboBox has its own built-in wheel behavior; this guarantees our step
        // logic always runs and has the final say over the box's value.
        SizeBox.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, OnSizeWheel,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ApplyBrushSize(Canvas.BrushWidth);
        HardnessBox.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, OnHardnessWheel,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ApplyBrushHardness((int)Math.Round(Canvas.BrushHardness * 100));
        ToleranceBox.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, OnToleranceWheel,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        ApplyTolerance(Canvas.FillTolerance);

        BuildPanelManager();
        RebuildLayoutPresetsMenu();
        RebuildRecentFilesMenu();
        RebuildExportPresetsMenu();
        RebuildPluginsMenu();
        // Named handler (not a lambda) so it can be unsubscribed below - these are STATIC events,
        // so without this a MainView instance (and everything it closes over) would stay reachable
        // for the process's whole lifetime the moment a second one is ever created, not just while
        // this one is on screen.
        KawaPaint.Engine.Plugins.EffectRegistry.Changed += OnPluginRegistryChanged;
        KawaPaint.Engine.Plugins.ToolRegistry.Changed += OnPluginRegistryChanged;
        Unloaded += (_, _) =>
        {
            KawaPaint.Engine.Plugins.EffectRegistry.Changed -= OnPluginRegistryChanged;
            KawaPaint.Engine.Plugins.ToolRegistry.Changed -= OnPluginRegistryChanged;
            DisposeTimelineResources();
            _autosave?.Dispose();
            _autosave = null;
        };
        SetupRulers();
        BuildCommands();
        ApplyHistorySettings();
        ApplyDrawingSettings();
        SyncWheelToActiveColor();
        RefreshSwatches();
        LoadStartupDocument();
        Canvas.History.Changed += (_, _) => MarkDirty();
        // Posted rather than called inline: History.Changed can fire from inside HistoryList's
        // own SelectionChanged dispatch (a row click -> OnHistorySelected -> JumpToHistory ->
        // History.Changed), and RebuildHistoryPanel's Items.Clear() reentering that same dispatch
        // crashes Avalonia's SelectionModel (ArgumentOutOfRangeException deep in its internals -
        // reproduced and confirmed via a live repro before this fix). Posting lets the click's
        // own dispatch finish first. See the identical DocumentChanged/RebuildLayerPanel fix above.
        Canvas.History.Changed += (_, _) => Dispatcher.UIThread.Post(RebuildHistoryPanel);
        RebuildHistoryPanel();
        RebuildCustomDock();
        SetClean(null);
        SelectTool("Pencil");
        InitializeDemo();   // after BuildCommands: it hooks the registry's dispatch scope
        InitializeScript(); // shares that same dispatch scope hook - see InitializeDemo

        _autosave = new AutosaveService(_settings, () => _session);
        _autosave.Saved += name => StatusText.Text = $"Autosaved {name} at {DateTime.Now:HH:mm}";
        _autosave.Saved += _ => CommitGitProject(autosave: true);

        _configGit = new ConfigGitTracker(_settings);

        // Deferred: on desktop this needs a Window to own the dialog, which is only available
        // once this view is attached (OwnerWindow is null during the constructor).
        Loaded += OnFirstLoadedCheckRecovery;
    }

    private async void OnFirstLoadedCheckRecovery(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Loaded -= OnFirstLoadedCheckRecovery;

        var owner = OwnerWindow;
        if (owner is null) return;   // no recovery prompt in the browser build (see class remarks)

        var entries = AutosaveRecovery.FindAll();
        if (entries.Count == 0) return;

        var choice = await new RecoveryDialog(entries.Count).ShowDialog<RecoveryChoice>(owner);
        if (choice != RecoveryChoice.Restore)
        {
            AutosaveRecovery.DiscardAll();
            return;
        }

        try
        {
            var newest = entries[0];
            var doc = DocumentFile.Load(newest.Path);
            Canvas.SetDocument(doc);
            _session = new DocumentSession(doc, filePath: null, displayName: "Recovered document");
            _session.MarkDirty(DirtyReason.Structure);   // recovered work was never saved by the user
            UpdateTitle();
            StatusText.Text = "Restored autosaved work from " + newest.WrittenUtc.ToLocalTime();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Recovery failed: " + ex.Message;
        }
        finally
        {
            AutosaveRecovery.DiscardAll();
        }
    }

    // ---- unsaved-changes tracking ----------------------------------------

    private void MarkDirty()
    {
        _session?.MarkDirty();
        UpdateTitle();
    }

    /// <summary>
    /// Records that the document on screen matches what is on disk. Starts a new session when the
    /// document itself was replaced (New, Open), and marks the existing one saved otherwise.
    /// </summary>
    private void SetClean(IStorageFile? file)
    {
        _currentFile = file;

        var document = Canvas.Document;
        if (document is null) { UpdateTitle(); return; }

        string? localPath = LocalPathOf(file);

        if (_session is null || !ReferenceEquals(_session.Document, document))
        {
            // A previous session's recovery snapshots are now moot: either its document is the
            // one just opened cleanly (this branch fires on New/Open too), or it was abandoned.
            if (_session is not null) AutosaveRecovery.Discard(_session.SessionId);
            _session = new DocumentSession(document, localPath, file?.Name);
        }
        else
        {
            _session.MarkSaved(localPath, file?.Name);
            AutosaveRecovery.Discard(_session.SessionId);
        }

        // Only real project files on a real filesystem are worth remembering - an exported PNG
        // or a browser-sandboxed handle has nothing a "recent files" entry could reopen.
        if (localPath is not null && localPath.EndsWith(DocumentFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            _settings.AddRecentFile(localPath);
            RebuildRecentFilesMenu();
        }

        UpdateTitle();
    }

    /// <summary>Real filesystem path behind a picker result (file or folder), or null under the browser sandbox.</summary>
    private static string? LocalPathOf(IStorageItem? item)
    {
        if (item is null) return null;
        try { return item.Path.IsAbsoluteUri && item.Path.IsFile ? item.Path.LocalPath : null; }
        catch { return null; }
    }

    private void UpdateTitle()
    {
        string name = _session?.DisplayName ?? _currentFile?.Name ?? "untitled";
        TitleChanged?.Invoke((IsDirty ? "* " : "") + name + " - KawaPaint");
    }

    /// <summary>Returns true if it's OK to proceed (saved or discarded); false if the user cancelled.
    /// Also used by MainWindow to gate the desktop close button.</summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsDirty) return true;
        if (OwnerWindow is not { } owner)
        {
            var body = new TextBlock
            {
                Text = "Save changes to the current image before continuing?",
                TextWrapping = TextWrapping.Wrap
            };
            var browserChoice = await ShowCanvasChoiceAsync("Unsaved Changes", body, SaveChoice.Cancel,
                new CanvasChoice<SaveChoice>("Cancel", SaveChoice.Cancel),
                new CanvasChoice<SaveChoice>("Discard", SaveChoice.Discard),
                new CanvasChoice<SaveChoice>("Save", SaveChoice.Save, true));
            return browserChoice switch
            {
                SaveChoice.Save => await SaveProjectAsync(),
                SaveChoice.Discard => true,
                _ => false
            };
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

    private void LoadStartupDocument()
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
        StatusText.Text = "Demo document - left-drag to draw, wheel zoom, middle/right-drag pan, Ctrl+Z undo";
    }

    private async void OnNew(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordSkipped("New Image");
        if (!await ConfirmDiscardAsync()) return;

        int w, h;
        double dpi;
        bool transparent;
        if (OwnerWindow is { } owner)
        {
            var dlg = new NewImageDialog();
            if (!await dlg.ShowDialog<bool>(owner)) return;
            (w, h, dpi, transparent) = (dlg.ResultWidth, dlg.ResultHeight, dlg.ResultDpi, dlg.Transparent);
        }
        else
        {
            var values = await ShowCanvasNewImageAsync();
            if (values is null) return;
            (w, h, dpi, transparent) = (values.Width, values.Height, values.Dpi, values.Transparent);
        }

        var doc = new Document(w, h) { Dpi = dpi };
        var bg = doc.AddLayer("Background");
        if (!transparent) bg.Surface.Clear(ColorBgra.White);
        Canvas.SetDocument(doc);
        SetClean(null);
        StatusText.Text = $"New {w}×{h} document";
    }

    private async void OnOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordSkipped("Open Image");
        if (!await ConfirmDiscardAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open image",
            AllowMultiple = false,
            FileTypeFilter = BuildOpenFilters()
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenReadAsync();
            using var sourceBuffer = new MemoryStream();
            await stream.CopyToAsync(sourceBuffer);
            byte[] sourceBytes = sourceBuffer.ToArray();
            using var decodeStream = new MemoryStream(sourceBytes);
            var frames = CodecRegistry.DecodeFrames(decodeStream, file.Name);
            var doc = new Document(frames[0].Surface.Width, frames[0].Surface.Height);
            doc.ExifTiff = ExifPreserver.ExtractTiff(sourceBytes);
            string baseName = System.IO.Path.GetFileNameWithoutExtension(file.Name);
            for (int index = 0; index < frames.Count; index++)
            {
                DecodedImageFrame frame = frames[index];
                string name = frames.Count == 1 ? baseName : $"Frame {index + 1}";
                int duration = Math.Max(10, frame.DurationMs);
                if (index == 0)
                {
                    doc.AddLayer(new Layer(frame.Surface, baseName));
                    doc.ActiveFrame.Name = name;
                    doc.ActiveFrame.DurationMs = duration;
                }
                else
                {
                    doc.AddFrame(new DocumentFrame(new[] { new Layer(frame.Surface, baseName) }, name, duration),
                        makeActive: false);
                }
            }
            doc.SetActiveFrame(0);
            Canvas.SetDocument(doc);
            SetClean(null);   // imported image has no project file yet
            StatusText.Text = frames.Count == 1
                ? $"{file.Name} - {doc.Width}×{doc.Height}"
                : $"{file.Name} - {doc.Width}×{doc.Height}, {frames.Count} timeline frames";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Open failed: " + ex.Message;
        }
    }

    /// <summary>
    /// File-dialog filters for the formats this build can actually read. Generated rather than
    /// hardcoded, because optional codecs (JPEG 2000, JPEG XL) are absent on some platforms and
    /// offering a format the app cannot open would only produce a failure later.
    /// </summary>
    private static FilePickerFileType[] BuildOpenFilters()
    {
        var decoders = CodecRegistry.Decoders.ToList();
        var everything = new FilePickerFileType("All supported images")
        {
            Patterns = decoders.SelectMany(c => c.Extensions).Select(ext => "*" + ext).ToArray()
        };

        return decoders
            .Select(c => new FilePickerFileType(c.DisplayName)
            {
                Patterns = c.Extensions.Select(ext => "*" + ext).ToArray()
            })
            .Prepend(everything)
            .ToArray();
    }

    private static FilePickerFileType[] BuildSaveFilters()
        => CodecRegistry.Encoders
            .Select(c => new FilePickerFileType(c.DisplayName)
            {
                Patterns = c.Extensions.Select(ext => "*" + ext).ToArray()
            })
            .ToArray();

    private async void OnOpenProject(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordSkipped("Open Project");
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
            StatusText.Text = $"{file.Name} - {doc.FrameCount} frame(s), {doc.LayerCount} active layer(s)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Open project failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Links the open document to a folder that KawaPaint mirrors into as the exploded (git-diffable)
    /// format and commits to on save/autosave, per GitSettings. Independent of the primary .kwp
    /// file this document may also be saved to -- linking doesn't change what Ctrl+S writes there.
    /// </summary>
    private async Task OnLinkGitProjectAsync()
    {
        if (_session is null) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose (or create) a folder to track this project in git",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        string? path = folder is null ? null : LocalPathOf(folder);
        if (path is null) return;

        if (!GitService.EnsureRepository(path, out string? error))
        {
            StatusText.Text = "Could not set up git repository: " + error;
            return;
        }

        _session.SetGitProjectDirectory(path);
        CommitGitProject(autosave: false); // confirms the link actually works, right away
        StatusText.Text = "Linked git project folder: " + path;
    }

    /// <summary>
    /// Mirrors the current document into the linked git project folder (if any) and commits,
    /// gated on GitSettings.Enabled/TrackProjects/CommitOnSave/CommitOnAutosave. A no-op for the
    /// large majority of documents, which have no linked folder at all.
    /// </summary>
    private void CommitGitProject(bool autosave)
    {
        if (Canvas.Document is null || _session?.GitProjectDirectory is not { } dir) return;

        var git = _settings.Settings.Git;
        if (!git.Enabled || !git.TrackProjects) return;
        if (autosave ? !git.CommitOnAutosave : !git.CommitOnSave) return;

        try
        {
            DocumentFile.SaveExploded(Canvas.Document, dir);
            string message = (autosave ? "Autosave" : "Save") + $": {_session.DisplayName}";
            GitService.CommitAll(dir, message, out _);
        }
        catch { /* a git commit failing must never interrupt the user's actual work */ }
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
            CommitGitProject(autosave: false);
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
            FileTypeChoices = BuildSaveFilters()
        });
        if (file is null) return;

        var options = EncodeOptions.Default;
        string? codecId = CodecRegistry.FindByExtension(file.Name)?.Id;
        if (codecId is "jpeg" or "webp" && OwnerWindow is { } owner)
        {
            var dlg = new SaveOptionsDialog(codecId);
            if (!await dlg.ShowDialog<bool>(owner)) return;
            options = dlg.ResultOptions;
        }

        try
        {
            using var flat = Canvas.Document.Flatten();
            using var encoded = new MemoryStream();
            CodecRegistry.Encode(flat, encoded, file.Name, options);
            byte[] bytes = ExifPreserver.Inject(encoded.ToArray(), Canvas.Document.ExifTiff,
                flat.Width, flat.Height);
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(bytes);
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
        RecordAction("effect." + tag);   // parameterless: fully reproducible from the tag alone

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
        Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, fx.Name));
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
        RecordSkipped("Resize Image");
        if (OwnerWindow is not { } owner)
        {
            var values = await ShowCanvasSizeFormAsync("Resize Image", doc.Width, doc.Height);
            if (values is null) return;
            int browserWidth = values.Width, browserHeight = values.Height;
            if (browserWidth == doc.Width && browserHeight == doc.Height) return;
            ApplyDocumentOp("Resize Image", d => DocumentOps.Resize(d, browserWidth, browserHeight));
            StatusText.Text = $"Resized to {browserWidth}×{browserHeight}";
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

    private async void OnCanvasSize(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null) return;
        RecordSkipped("Canvas Size");
        if (OwnerWindow is not { } owner)
        {
            var values = await ShowCanvasCanvasSizeAsync(doc.Width, doc.Height);
            if (values is null) return;
            if (values.Width == doc.Width && values.Height == doc.Height) return;
            ApplyDocumentOp("Canvas Size", d => DocumentOps.ResizeCanvas(
                d, values.Width, values.Height, values.Anchor));
            StatusText.Text = $"Canvas resized to {values.Width}×{values.Height}";
            return;
        }
        var dlg = new CanvasSizeDialog(doc.Width, doc.Height);
        if (await dlg.ShowDialog<bool>(owner))
        {
            int w = dlg.ResultWidth, h = dlg.ResultHeight;
            if (w == doc.Width && h == doc.Height) return;
            var anchor = dlg.ResultAnchor;
            ApplyDocumentOp("Canvas Size", d => DocumentOps.ResizeCanvas(d, w, h, anchor));
            StatusText.Text = $"Canvas resized to {w}×{h}";
        }
    }

    private void OnCropToSelection(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null || Canvas.Selection is not { IsActive: true } sel) { StatusText.Text = "Crop needs an active selection"; return; }

        var (x, y, w, h) = sel.GetBounds();
        if (w <= 0 || h <= 0) return;
        RecordParameterizedAction("image.crop", new double[] { x, y, w, h });
        ApplyDocumentOp("Crop to Selection", d => DocumentOps.Crop(d, x, y, w, h));
        StatusText.Text = $"Cropped to {w}×{h}";
    }

    private void OnFlipH(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document; if (doc is null) return;
        RecordAction("image.flipH");
        DocumentOps.FlipHorizontal(doc);
        Canvas.History.Push(new DelegateMemento("Flip Horizontal",
            () => DocumentOps.FlipHorizontal(doc), () => DocumentOps.FlipHorizontal(doc)));
        RefreshDocument();
        StatusText.Text = "Flipped horizontally";
    }

    private void OnFlipV(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document; if (doc is null) return;
        RecordAction("image.flipV");
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
        RecordAction(cw ? "image.rotateCW" : "image.rotateCCW");
        string name = cw ? "Rotate 90° CW" : "Rotate 90° CCW";
        ApplyDocumentOp(name, d => DocumentOps.Rotate90(d, cw));
        StatusText.Text = cw ? "Rotated 90° CW" : "Rotated 90° CCW";
    }

    private void OnFlatten(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null) return;
        if (doc.LayerCount <= 1) { StatusText.Text = "Already a single layer"; return; }
        RecordAction("image.flatten");
        ApplyDocumentOp("Flatten Image", DocumentOps.Flatten);
        StatusText.Text = "Flattened";
    }

    private void OnSelectNone(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordAction("select.none");
        Canvas.Selection?.SelectNone();
        Canvas.NotifySelectionChanged();
        StatusText.Text = "Selection cleared";
    }

    private void OnSelectAll(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordAction("select.all");
        Canvas.Selection?.SelectAll();
        Canvas.NotifySelectionChanged();
        StatusText.Text = "Selected all";
    }

    private void OnInvertSelection(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordAction("select.invert");
        Canvas.Selection?.Invert();
        Canvas.NotifySelectionChanged();
        StatusText.Text = "Selection inverted";
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

            "noise" => CreateNoiseDialog(),

            "bulge" => new AdjustmentDialog(Canvas, "Bulge", new[]
            {
                new AdjustmentDialog.SliderSpec("Amount", -200, 100, 45, "0")
            }, v => new BulgeEffect(v[0])),

            "twist" => new AdjustmentDialog(Canvas, "Twist", new[]
            {
                new AdjustmentDialog.SliderSpec("Amount", -200, 200, 30, "0"),
                new AdjustmentDialog.SliderSpec("Size", 0.01, 2.0, 1.0, "0.00")
            }, v => new TwistEffect(v[0], v[1])),

            "polarinv" => new AdjustmentDialog(Canvas, "Polar Inversion", new[]
            {
                new AdjustmentDialog.SliderSpec("Amount", -4, 4, 1, "0.00")
            }, v => new PolarInversionEffect(v[0])),

            "tile" => new AdjustmentDialog(Canvas, "Tile", new[]
            {
                new AdjustmentDialog.SliderSpec("Rotation", -180, 180, 30, "0"),
                new AdjustmentDialog.SliderSpec("Square Size", 1, 300, 40, "0"),
                new AdjustmentDialog.SliderSpec("Curvature", -100, 100, 8, "0")
            }, v => new TileEffect(v[0], v[1], v[2])),

            "frostedglass" => CreateFrostedGlassDialog(),

            "pixelate" => new AdjustmentDialog(Canvas, "Pixelate", new[]
            {
                new AdjustmentDialog.SliderSpec("Cell Size", 1, 100, 8, "0")
            }, v => new PixelateEffect((int)v[0])),

            "median" => new AdjustmentDialog(Canvas, "Median", new[]
            {
                new AdjustmentDialog.SliderSpec("Radius", 1, 30, 5, "0"),
                new AdjustmentDialog.SliderSpec("Percentile", 0, 100, 50, "0")
            }, v => new MedianEffect((int)v[0], (int)v[1])),

            "outline" => new AdjustmentDialog(Canvas, "Outline", new[]
            {
                new AdjustmentDialog.SliderSpec("Thickness", 1, 30, 3, "0"),
                new AdjustmentDialog.SliderSpec("Intensity", 0, 100, 50, "0")
            }, v => new OutlineEffect((int)v[0], (int)v[1])),

            "relief" => new AdjustmentDialog(Canvas, "Relief", new[]
            {
                new AdjustmentDialog.SliderSpec("Angle", -180, 180, 45, "0")
            }, v => new ReliefEffect(v[0])),

            "vignette" => new AdjustmentDialog(Canvas, "Vignette", new[]
            {
                new AdjustmentDialog.SliderSpec("Amount", 0, 1, 1, "0.00"),
                new AdjustmentDialog.SliderSpec("Radius", 0.1, 4.0, 0.5, "0.00")
            }, v => new VignetteEffect(v[0], v[1])),

            "dents" => CreateDentsDialog(),

            "reducenoise" => new AdjustmentDialog(Canvas, "Reduce Noise", new[]
            {
                new AdjustmentDialog.SliderSpec("Radius", 1, 30, 6, "0"),
                new AdjustmentDialog.SliderSpec("Strength", 0, 1, 0.4, "0.00")
            }, v => new ReduceNoiseEffect((int)v[0], v[1])),

            "motionblur" => new AdjustmentDialog(Canvas, "Motion Blur", new[]
            {
                new AdjustmentDialog.SliderSpec("Angle", -180, 180, 25, "0"),
                new AdjustmentDialog.SliderSpec("Distance", 1, 100, 10, "0")
            }, v => new MotionBlurEffect(v[0], (int)v[1])),

            "radialblur" => new AdjustmentDialog(Canvas, "Radial Blur", new[]
            {
                new AdjustmentDialog.SliderSpec("Angle", 0, 90, 5, "0")
            }, v => new RadialBlurEffect(v[0])),

            "zoomblur" => new AdjustmentDialog(Canvas, "Zoom Blur", new[]
            {
                new AdjustmentDialog.SliderSpec("Amount", 0, 100, 10, "0")
            }, v => new ZoomBlurEffect((int)v[0])),

            "surfaceblur" => new AdjustmentDialog(Canvas, "Surface Blur", new[]
            {
                new AdjustmentDialog.SliderSpec("Radius", 1, 30, 6, "0"),
                new AdjustmentDialog.SliderSpec("Threshold", 1, 100, 15, "0")
            }, v => new SurfaceBlurEffect((int)v[0], (int)v[1])),

            "unfocus" => new AdjustmentDialog(Canvas, "Unfocus", new[]
            {
                new AdjustmentDialog.SliderSpec("Radius", 1, 30, 4, "0")
            }, v => new UnfocusEffect((int)v[0])),

            "fragment" => new AdjustmentDialog(Canvas, "Fragment", new[]
            {
                new AdjustmentDialog.SliderSpec("Fragments", 2, 50, 4, "0"),
                new AdjustmentDialog.SliderSpec("Rotation", 0, 360, 0, "0"),
                new AdjustmentDialog.SliderSpec("Distance", 0, 100, 8, "0")
            }, v => new FragmentEffect((int)v[0], v[1], (int)v[2])),

            "clouds" => new AdjustmentDialog(Canvas, "Clouds", new[]
            {
                new AdjustmentDialog.SliderSpec("Scale", 2, 500, 200, "0"),
                new AdjustmentDialog.SliderSpec("Power", 0, 1, 0.5, "0.00")
            }, v => new CloudsEffect((int)v[0], v[1], 0, Canvas.BrushColor, Canvas.SecondaryColor)),

            "julia" => new AdjustmentDialog(Canvas, "Julia Fractal", new[]
            {
                new AdjustmentDialog.SliderSpec("Factor", 1, 10, 4, "0.0"),
                new AdjustmentDialog.SliderSpec("Zoom", 0.1, 20, 1, "0.00"),
                new AdjustmentDialog.SliderSpec("Angle", -180, 180, 0, "0")
            }, v => new JuliaFractalEffect(v[0], v[1], v[2])),

            "mandelbrot" => new AdjustmentDialog(Canvas, "Mandelbrot Fractal", new[]
            {
                new AdjustmentDialog.SliderSpec("Factor", 1, 10, 1, "0"),
                new AdjustmentDialog.SliderSpec("Zoom", 0, 100, 10, "0"),
                new AdjustmentDialog.SliderSpec("Angle", -180, 180, 0, "0")
            }, v => new MandelbrotFractalEffect((int)v[0], v[1], v[2], v[3] != 0),
                new[] { new AdjustmentDialog.CheckboxSpec("Invert colors") }),

            "glow" => new AdjustmentDialog(Canvas, "Glow", new[]
            {
                new AdjustmentDialog.SliderSpec("Radius", 1, 20, 6, "0"),
                new AdjustmentDialog.SliderSpec("Brightness", -100, 100, 10, "0"),
                new AdjustmentDialog.SliderSpec("Contrast", -100, 100, 10, "0")
            }, v => new GlowEffect((int)v[0], (int)v[1], (int)v[2])),

            "redeye" => new AdjustmentDialog(Canvas, "Red Eye Removal", new[]
            {
                new AdjustmentDialog.SliderSpec("Tolerance", 0, 100, 70, "0"),
                new AdjustmentDialog.SliderSpec("Saturation", 0, 100, 90, "0")
            }, v => new RedEyeRemoveEffect((int)v[0], (int)v[1])),

            "softenportrait" => new AdjustmentDialog(Canvas, "Soften Portrait", new[]
            {
                new AdjustmentDialog.SliderSpec("Softness", 0, 10, 5, "0"),
                new AdjustmentDialog.SliderSpec("Lighting", -20, 20, 0, "0"),
                new AdjustmentDialog.SliderSpec("Warmth", 0, 20, 10, "0")
            }, v => new SoftenPortraitEffect((int)v[0], (int)v[1], (int)v[2])),

            "inksketch" => new AdjustmentDialog(Canvas, "Ink Sketch", new[]
            {
                new AdjustmentDialog.SliderSpec("Ink Outline", 0, 99, 50, "0"),
                new AdjustmentDialog.SliderSpec("Coloring", 0, 100, 50, "0")
            }, v => new InkSketchEffect((int)v[0], (int)v[1])),

            "pencilsketch" => new AdjustmentDialog(Canvas, "Pencil Sketch", new[]
            {
                new AdjustmentDialog.SliderSpec("Pencil Tip Size", 1, 20, 2, "0"),
                new AdjustmentDialog.SliderSpec("Color Range", -20, 20, 0, "0")
            }, v => new PencilSketchEffect((int)v[0], (int)v[1])),

            "oilpainting" => new AdjustmentDialog(Canvas, "Oil Painting", new[]
            {
                new AdjustmentDialog.SliderSpec("Brush Size", 1, 8, 3, "0"),
                new AdjustmentDialog.SliderSpec("Coarseness", 3, 255, 50, "0")
            }, v => new OilPaintingEffect((int)v[0], (int)v[1])),

            _ => new AdjustmentDialog(Canvas, "Gaussian Blur", new[]
            {
                new AdjustmentDialog.SliderSpec("Radius", 1, 30, 5, "0")
            }, v => new BoxBlurEffect((int)v[0]))
        };

        if (OwnerWindow is { } owner)
            await dlg.ShowDialog(owner);
        else
            await ShowCanvasWindowContentAsync(dlg, dlg.UseCanvasHost,
                dlg.CancelCanvasHost, dlg.BeginCanvasHost);
        if (dlg.CommittedValues is { } vals)
            RecordParameterizedAction("effect." + tag, vals, tag == "clouds"
                ? new[] { Canvas.BrushColor.ToHexString(), Canvas.SecondaryColor.ToHexString() }
                : null);
        StatusText.Text = dlg.Title ?? "Adjustment";
    }

    private AdjustmentDialog CreateNoiseDialog()
    {
        int seed = Random.Shared.Next();
        return new AdjustmentDialog(Canvas, "Add Noise", new[]
        {
            new AdjustmentDialog.SliderSpec("Amount", 0, 100, 25, "0")
        }, v => new NoiseEffect((int)v[0], seed), replayArgs: new double[] { seed });
    }

    private AdjustmentDialog CreateFrostedGlassDialog()
    {
        int seed = Random.Shared.Next();
        return new AdjustmentDialog(Canvas, "Frosted Glass", new[]
        {
            new AdjustmentDialog.SliderSpec("Min Radius", 0, 50, 0, "0.0"),
            new AdjustmentDialog.SliderSpec("Max Radius", 0, 50, 3, "0.0"),
            new AdjustmentDialog.SliderSpec("Samples", 1, 8, 2, "0")
        }, v => new FrostedGlassEffect(v[0], v[1], (int)v[2], seed), replayArgs: new double[] { seed });
    }

    private AdjustmentDialog CreateDentsDialog()
    {
        int seed = Random.Shared.Next();
        return new AdjustmentDialog(Canvas, "Dents", new[]
        {
            new AdjustmentDialog.SliderSpec("Scale", 1, 200, 25, "0"),
            new AdjustmentDialog.SliderSpec("Refraction", 0, 200, 50, "0"),
            new AdjustmentDialog.SliderSpec("Roughness", 0, 100, 10, "0"),
            new AdjustmentDialog.SliderSpec("Tension", 0, 100, 10, "0")
        }, v => new DentsEffect(v[0], v[1], v[2], v[3], seed), replayArgs: new double[] { seed });
    }

    private async void OnCurves(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas.ActiveLayer is null) return;
        var dialog = new CurvesDialog(Canvas);
        if (OwnerWindow is { } owner)
            await dialog.ShowDialog(owner);
        else
            await ShowCanvasWindowContentAsync(dialog, dialog.UseCanvasHost,
                dialog.CancelCanvasHost, dialog.BeginCanvasHost);
        if (dialog.CommittedLut is { } lut)
            RecordParameterizedAction("effect.curves", lut.Select(value => (double)value).ToArray());
        StatusText.Text = "Curves";
    }

    // These also exist as CommandRegistry commands, but a menu item or toolbar button reaches the
    // handler directly without passing through the registry - so each one records for itself. The
    // registry path suppresses the duplicate note (see DemoRecorder.Suppress).
    private void OnUndo(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { RecordAction("edit.undo"); Canvas.Undo(); }
    private void OnRedo(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { RecordAction("edit.redo"); Canvas.Redo(); }
    private void OnZoomIn(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { RecordAction("view.zoomIn"); Canvas.ZoomIn(); }
    private void OnZoomOut(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { RecordAction("view.zoomOut"); Canvas.ZoomOut(); }
    private void OnZoomFit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { RecordAction("view.zoomFit"); Canvas.ZoomToFit(); }
    private void OnZoomActual(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { RecordAction("view.zoomActual"); Canvas.ZoomActual(); }

    // ---- commands ---------------------------------------------------------
    //
    // Every keyboard-reachable action is registered here rather than being switched on inside the
    // key handler, so a shortcut can be rebound from settings and the customizable dock has a
    // list of actions to offer.

    private void BuildCommands()
    {
        var empty = new Avalonia.Interactivity.RoutedEventArgs();

        void Add(string id, string label, string category, Action run, KeyGesture? gesture = null,
                 bool suppressInTextInput = false, string? icon = null, KeyGesture? altGesture = null)
            => _commands.Register(id, label, run, category, icon, gesture,
                                  canExecute: null, suppressInTextInput: suppressInTextInput,
                                  alternateGesture: altGesture);

        KeyGesture Ctrl(Key key) => new(key, KeyModifiers.Control);
        KeyGesture CtrlShift(Key key) => new(key, KeyModifiers.Control | KeyModifiers.Shift);
        KeyGesture CtrlAlt(Key key) => new(key, KeyModifiers.Control | KeyModifiers.Alt);
        KeyGesture Bare(Key key) => new(key);

        // File
        Add("file.new", "New", "File", () => OnNew(this, empty), Ctrl(Key.N));
        Add("file.open", "Open Image", "File", () => OnOpen(this, empty), Ctrl(Key.O));
        Add("file.openProject", "Open Project", "File", () => OnOpenProject(this, empty), CtrlShift(Key.O));
        Add("file.saveProject", "Save Project", "File", () => OnSaveProject(this, empty), Ctrl(Key.S));
        Add("file.export", "Export Flattened", "File", () => OnSaveAs(this, empty), CtrlShift(Key.S));
        Add("file.linkGitProject", "Link Git Project Folder...", "File", () => _ = OnLinkGitProjectAsync());

        // Edit - these stay with a focused text field, which has its own undo and select-all.
        Add("edit.undo", "Undo", "Edit", () => Canvas.Undo(), Ctrl(Key.Z), suppressInTextInput: true, icon: "Undo");
        Add("edit.redo", "Redo", "Edit", () => Canvas.Redo(), CtrlShift(Key.Z),
            suppressInTextInput: true, icon: "Redo", altGesture: Ctrl(Key.Y));

        // Clipboard - cut/copy/paste stay with a focused text field the same way undo/redo do.
        Add("edit.cut", "Cut", "Edit", () => OnCut(this, empty), Ctrl(Key.X), suppressInTextInput: true, icon: "Cut");
        Add("edit.copy", "Copy", "Edit", () => OnCopy(this, empty), Ctrl(Key.C), suppressInTextInput: true, icon: "Copy");
        Add("edit.copyMerged", "Copy Merged", "Edit", () => OnCopyMerged(this, empty), CtrlShift(Key.C), suppressInTextInput: true);
        Add("edit.paste", "Paste", "Edit", () => OnPaste(this, empty), Ctrl(Key.V), suppressInTextInput: true, icon: "Paste");
        Add("edit.pasteIntoNewLayer", "Paste Into New Layer", "Edit", () => OnPasteIntoNewLayer(this, empty), CtrlShift(Key.V), suppressInTextInput: true);
        Add("edit.pasteIntoNewImage", "Paste Into New Image", "Edit", () => OnPasteIntoNewImage(this, empty), CtrlAlt(Key.V), suppressInTextInput: true);

        // Select
        Add("select.all", "Select All", "Select", () => OnSelectAll(this, empty), Ctrl(Key.A), suppressInTextInput: true);
        Add("select.none", "Deselect", "Select", () => OnSelectNone(this, empty), Ctrl(Key.D));
        Add("select.invert", "Invert Selection", "Select", () => OnInvertSelection(this, empty), Ctrl(Key.I));
        Add("select.fill", "Fill Selection", "Select", () => OnFillSelection(this, empty), Ctrl(Key.F));
        Add("select.erase", "Erase Selection", "Select", () => OnEraseSelection(this, empty), Bare(Key.Delete), suppressInTextInput: true);

        // View
        Add("view.zoomIn", "Zoom In", "View", () => Canvas.ZoomIn(), Ctrl(Key.OemPlus));
        Add("view.zoomInNumpad", "Zoom In", "View", () => Canvas.ZoomIn(), Ctrl(Key.Add));
        Add("view.zoomOut", "Zoom Out", "View", () => Canvas.ZoomOut(), Ctrl(Key.OemMinus));
        Add("view.zoomOutNumpad", "Zoom Out", "View", () => Canvas.ZoomOut(), Ctrl(Key.Subtract));
        Add("view.zoomFit", "Fit to Window", "View", () => Canvas.ZoomToFit(), Ctrl(Key.D0));
        Add("view.zoomActual", "Actual Size", "View", () => Canvas.ZoomActual(), Ctrl(Key.D1));

        // Panels
        foreach (var panel in _panels.Panels)
        {
            var id = panel.Id;
            // The custom dock is meant to be summonable on demand, so it ships with a default
            // key combo out of the box rather than requiring the user to bind one by hand.
            var gesture = id == "Dock" ? Ctrl(Key.OemTilde) : null;
            Add($"panel.toggle.{id}", $"Toggle {panel.Title} Panel", "Panels",
                () => _panels.ToggleVisible(id), gesture, icon: panel.IconName);
            Add($"panel.float.{id}", $"Float {panel.Title} Panel", "Panels",
                () => _panels.ToggleFloat(id));
        }

        // Tools. Bare letters, so they must not fire while a text field has focus.
        foreach (var (key, tag, label) in new (Key, string, string)[]
        {
            (Key.P, "Pencil", "Pencil"), (Key.B, "Brush", "Paintbrush"),
            (Key.E, "Eraser", "Eraser"), (Key.F, "Fill", "Paint Bucket"),
            (Key.K, "Pick", "Color Picker"), (Key.L, "Line", "Line"), (Key.R, "Rect", "Rectangle"),
            (Key.O, "Ellipse", "Ellipse"), (Key.G, "Gradient", "Gradient"), (Key.T, "Text", "Text"),
            (Key.M, "Move", "Move"), (Key.C, "Clone", "Clone Stamp"), (Key.N, "Recolor", "Recolor"),
            (Key.U, "RoundRect", "Rounded Rectangle"), (Key.D, "Freeform", "Freeform Shape"),
            (Key.H, "Star", "Star"), (Key.A, "Arrow", "Arrow")
        })
        {
            string toolTag = tag;
            Add($"tool.{toolTag}", label, "Tools", () => SelectTool(toolTag), Bare(key),
                suppressInTextInput: true, icon: toolTag);
        }

        // One key cycles the three selection tools, as in Paint.NET.
        Add("tool.selectCycle", "Selection Tool", "Tools", () => SelectTool(_currentToolTag switch
        {
            "RectSel" => "EllipseSel",
            "EllipseSel" => "Lasso",
            "Lasso" => "Wand",
            _ => "RectSel"
        }), Bare(Key.S), suppressInTextInput: true);

        _commands.ReloadBindings(_settings.Settings.Workspace);
    }

    /// <summary>Points the undo stack at the configured limits and its on-disk spill cache.</summary>
    private void ApplyHistorySettings()
    {
        var history = _settings.Settings.History;
        Canvas.History.MaxSteps = Math.Max(0, history.MaxSteps);
        Canvas.History.MemoryBudgetBytes = Math.Max(0L, history.MemoryBudgetMegabytes) * 1024 * 1024;
        Canvas.History.SpillDirectory = history.SpillToDisk ? AppPaths.HistorySpillDirectory : null;
    }

    private void ApplyDrawingSettings()
    {
        var drawing = _settings.Settings.Drawing;
        Canvas.PencilPressure = drawing.PencilPressure;
        Canvas.PaintbrushPressure = drawing.PaintbrushPressure;
        Canvas.EraserPressure = drawing.EraserPressure;
        Canvas.PenEraserEnabled = drawing.PenEraserEnabled;
        Canvas.TouchNavigationEnabled = drawing.TouchNavigationEnabled;
    }

    // ---- modular panel layout --------------------------------------------
    //
    // Placement, dragging, resizing and persistence all live in PanelManager; this section only
    // declares which panels exist and forwards the AXAML button clicks.

    private void BuildPanelManager()
    {
        var workspace = _settings.Settings.Workspace;
        if (!workspace.Layouts.TryGetValue(workspace.ActiveLayout, out var layout))
        {
            layout = new WorkspaceLayout();
            workspace.Layouts[workspace.ActiveLayout] = layout;
        }

        _panels = new PanelManager(RootDock, FloatingLayer, CanvasArea, layout);

        _panels.Register(new PanelDescriptor("Tools", "Tools", ToolsBorder)
        {
            IconName = "PanelTools",
            DockedChrome = new Control[] { ToolsHeader },
            DefaultPlace = PanelPlace.Left,
            DefaultDockSize = 70,
            DefaultFloatX = 90,
            DefaultFloatY = 60,
            MinWidth = 60,
            MinHeight = 120
        });
        _panels.Register(new PanelDescriptor("Colors", "Colors", ColorsBorder)
        {
            IconName = "PanelColors",
            DockedChrome = new Control[] { ColorsTitle, ColorsHeader },
            DefaultPlace = PanelPlace.Bottom,
            DefaultFloatX = 90,
            DefaultFloatY = 420,
            MinWidth = 220,
            MinHeight = 60
        });
        _panels.Register(new PanelDescriptor("ColorWheel", "Color", ColorWheelBorder)
        {
            IconName = "PanelColorWheel",
            DockedChrome = new Control[] { ColorWheelHeader },
            DefaultPlace = PanelPlace.Right,
            DefaultDockSize = 190,
            DefaultFloatX = 520,
            DefaultFloatY = 60,
            MinWidth = 170,
            MinHeight = 240
        });
        _panels.Register(new PanelDescriptor("Layers", "Layers", LayersBorder)
        {
            IconName = "PanelLayers",
            DockedChrome = new Control[] { LayersHeader },
            DefaultPlace = PanelPlace.Right,
            DefaultDockSize = 220,
            DefaultFloatX = 760,
            DefaultFloatY = 60,
            MinWidth = 180,
            MinHeight = 200
        });
        _panels.Register(new PanelDescriptor("History", "History", HistoryBorder)
        {
            IconName = "PanelHistory",
            DockedChrome = new Control[] { HistoryHeader },
            DefaultPlace = PanelPlace.Hidden,
            DefaultDockSize = 220,
            DefaultFloatX = 940,
            DefaultFloatY = 60,
            MinWidth = 180,
            MinHeight = 200
        });
        _panels.Register(new PanelDescriptor("Timeline", "Timeline", TimelineBorder)
        {
            IconName = "PanelTimeline",
            DockedChrome = new Control[] { TimelineHeader },
            DefaultPlace = PanelPlace.Bottom,
            DefaultDockSize = 150,
            DefaultFloatX = 260,
            DefaultFloatY = 380,
            DefaultFloatWidth = 660,
            DefaultFloatHeight = 230,
            MinWidth = 300,
            MinHeight = 150
        });
        _panels.Register(new PanelDescriptor("Dock", "Dock", DockBorder)
        {
            IconName = "PanelDock",
            DockedChrome = new Control[] { DockHeader },
            // Hidden until summoned (key combo / top-right icon); WorkspaceLayout.For then makes
            // its first appearance Floating rather than docked to a side - see spec: "summoned...
            // floating window by default".
            DefaultPlace = PanelPlace.Hidden,
            DefaultDockSize = 220,
            DefaultFloatX = 400,
            DefaultFloatY = 300,
            MinWidth = 160,
            MinHeight = 100
        });

        _panels.LayoutChanged += (_, _) =>
        {
            RefreshPanelToggleButtons();
            PersistLayout();
        };

        _panels.Apply();
        RefreshPanelToggleButtons();
    }

    private void OnPanelPlace(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string? tag = sender switch
        {
            Button b => b.Tag as string,
            MenuItem m => m.Tag as string,
            _ => null
        };
        if (tag is null) return;

        // Tag format is "<panelId>:<place>", e.g. "Layers:Floating".
        var parts = tag.Split(':');
        if (parts.Length != 2) return;
        if (!Enum.TryParse<PanelPlace>(parts[1], ignoreCase: true, out var place)) return;

        _panels.SetPlace(parts[0], place);
    }

    /// <summary>Top-right icon toggle: hides a visible panel, or restores a hidden one to
    /// wherever it was last shown (its dock side, or Floating at its last position).</summary>
    private void OnPanelToggle(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string id }) return;
        RecordAction("panel.toggle." + id);
        _panels.ToggleVisible(id);
    }

    /// <summary>Per-panel float button: undocks a docked panel, or docks a floating one.</summary>
    private void OnPanelFloatToggle(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string id }) return;
        RecordAction("panel.float." + id);
        _panels.ToggleFloat(id);
    }

    private void RefreshPanelToggleButtons()
    {
        ToggleTools.IsChecked = _panels.IsVisible("Tools");
        ToggleColors.IsChecked = _panels.IsVisible("Colors");
        ToggleColorWheel.IsChecked = _panels.IsVisible("ColorWheel");
        ToggleLayers.IsChecked = _panels.IsVisible("Layers");
        ToggleHistory.IsChecked = _panels.IsVisible("History");
        ToggleTimeline.IsChecked = _panels.IsVisible("Timeline");
        ToggleDock.IsChecked = _panels.IsVisible("Dock");

        RefreshFloatButton(FloatToolsBtn, "Tools");
        RefreshFloatButton(FloatColorsBtn, "Colors");
        RefreshFloatButton(FloatColorWheelBtn, "ColorWheel");
        RefreshFloatButton(FloatLayersBtn, "Layers");
        RefreshFloatButton(FloatHistoryBtn, "History");
        RefreshFloatButton(FloatTimelineBtn, "Timeline");
        RefreshFloatButton(FloatDockBtn, "Dock");
    }

    private void RefreshFloatButton(ToggleButton btn, string id)
    {
        bool floating = _panels.IsFloating(id);
        btn.IsChecked = floating;
        ToolTip.SetTip(btn, floating ? "Dock this panel" : "Float this panel");
    }

    // ---- rulers --------------------------------------------------------------

    private void SetupRulers()
    {
        HRuler.Target = Canvas;
        VRuler.Target = Canvas;

        var workspace = _settings.Settings.Workspace;
        ApplyRulerUnit(workspace.RulerUnit);
        ApplyRulerVisibility(workspace.ShowRulers);

        Canvas.ViewChanged += () => { HRuler.InvalidateVisual(); VRuler.InvalidateVisual(); };
        Canvas.DocumentChanged += (_, _) => { HRuler.InvalidateVisual(); VRuler.InvalidateVisual(); };
        Canvas.CursorMoved += (x, y) =>
        {
            HRuler.CursorPosition = x;
            VRuler.CursorPosition = y;
            HRuler.InvalidateVisual();
            VRuler.InvalidateVisual();
        };
    }

    private void ApplyRulerUnit(RulerUnit unit)
    {
        HRuler.Unit = unit;
        VRuler.Unit = unit;
        RulerCornerBtn.Content = RulerMath.Abbreviation(unit);
        HRuler.InvalidateVisual();
        VRuler.InvalidateVisual();
    }

    private void ApplyRulerVisibility(bool visible)
    {
        var lengths = visible ? new GridLength(18) : new GridLength(0);
        CanvasArea.RowDefinitions[0] = new RowDefinition(lengths);
        CanvasArea.ColumnDefinitions[0] = new ColumnDefinition(lengths);
        ToggleRulersItem.IsChecked = visible;
    }

    private void OnRulerCornerClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var workspace = _settings.Settings.Workspace;
        workspace.RulerUnit = workspace.RulerUnit switch
        {
            RulerUnit.Pixels => RulerUnit.Inches,
            RulerUnit.Inches => RulerUnit.Centimeters,
            _ => RulerUnit.Pixels
        };
        ApplyRulerUnit(workspace.RulerUnit);
        _settings.Save();
    }

    private void OnSetRulerUnit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<RulerUnit>(tag, out var unit)) return;
        RecordAction("view.rulerUnit." + tag);
        _settings.Settings.Workspace.RulerUnit = unit;
        ApplyRulerUnit(unit);
        _settings.Save();
    }

    private void OnToggleRulers(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordAction("view.rulers.toggle");
        var workspace = _settings.Settings.Workspace;
        workspace.ShowRulers = !workspace.ShowRulers;
        ApplyRulerVisibility(workspace.ShowRulers);
        _settings.Save();
    }

    // ---- customizable dock ----------------------------------------------------
    //
    // A user-assembled strip of command buttons and palette-color swatches, stored as plain
    // strings in WorkspaceSettings.DockCommands (see DockEntry for the encoding) so the dock
    // rides along with everything else settings already persists and, later, syncs.

    private void RebuildCustomDock()
    {
        DockContent.Children.Clear();
        var pinned = _settings.Settings.Workspace.DockCommands;

        if (pinned.Count == 0)
        {
            DockContent.Children.Add(new TextBlock
            {
                Text = "Empty - click the gear to add tools or colors.",
                Foreground = Brushes.Gray,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Width = 190
            });
            return;
        }

        foreach (string raw in pinned)
        {
            var entry = DockEntry.Parse(raw);
            Control control = entry.Kind == DockEntryKind.Color
                ? BuildDockColorButton(entry.Value)
                : BuildDockCommandButton(entry.Value);
            DockContent.Children.Add(control);
        }
    }

    private Button BuildDockCommandButton(string commandId)
    {
        var command = _commands.Find(commandId);
        var btn = new Button { Width = 32, Height = 32, Margin = new Thickness(2), Padding = new Thickness(2) };

        if (command is null)
        {
            // The command was removed (e.g. a stale entry from an older build) - keep the slot
            // visible but inert rather than silently dropping it, so the user can see and remove it.
            btn.Content = "?";
            btn.IsEnabled = false;
            ToolTip.SetTip(btn, commandId + " (unknown command)");
            return btn;
        }

        btn.Content = command.IconName is not null ? Icons.Create(command.IconName, 16) : new TextBlock
        {
            Text = command.Label.Length > 0 ? command.Label[..1] : "?",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(btn, command.Label);
        btn.Click += (_, _) => _commands.Execute(commandId);
        return btn;
    }

    /// <param name="hex">As stored by DockEntry, i.e. ColorBgra.ToHexString's AARRGGBB form.
    /// Parsed through ColorBgra so the alpha survives and so a hand-edited settings entry that
    /// isn't a colour at all leaves an inert slot instead of throwing while the dock is built.</param>
    private Button BuildDockColorButton(string hex)
    {
        if (!ColorBgra.TryParseHexString(hex, out var color))
        {
            var bad = new Button { Width = 32, Height = 32, Margin = new Thickness(2), Content = "?", IsEnabled = false };
            ToolTip.SetTip(bad, hex + " (not a colour)");
            return bad;
        }

        var btn = new Button
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(2),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)),
            Classes = { "swatch" }
        };
        ToolTip.SetTip(btn, color.ToDisplayHexString());
        btn.Click += (_, _) => SetForeground(color);
        return btn;
    }

    private async void OnCustomizeDock(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (OwnerWindow is not { } owner)
        {
            StatusText.Text = "Dock customization isn't available in the browser build yet";
            return;
        }

        var dlg = new DockEditorDialog(_commands.All, _palette.Colors, _settings.Settings.Workspace.DockCommands);
        if (!await dlg.ShowDialog<bool>(owner)) return;

        _settings.Settings.Workspace.DockCommands = dlg.ResultEntries.ToList();
        _settings.Save();
        RebuildCustomDock();
    }

    private void OnResetLayout(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var fresh = new WorkspaceLayout();
        _settings.Settings.Workspace.Layouts[_settings.Settings.Workspace.ActiveLayout] = fresh;
        _panels.SetLayout(fresh);
        RefreshPanelToggleButtons();
        PersistLayout();
    }

    /// <summary>
    /// Panel geometry changes arrive one per pointer move during a drag, so the write is
    /// coalesced onto the next dispatcher pass rather than hitting the disk on every frame.
    /// </summary>
    private void PersistLayout()
    {
        var workspace = _settings.Settings.Workspace;
        workspace.Layouts[workspace.ActiveLayout] = _panels.Layout;
        _settings.SaveDeferred();
    }

    // ---- recent files --------------------------------------------------------
    //
    // Desktop only: the MRU list is built from real filesystem paths (see SetClean), which the
    // browser sandbox never has. Reopening goes through StorageProvider.TryGetFileFromPathAsync
    // so it lands back on the exact same OnOpenProject code path as picking the file by hand.

    private void RebuildRecentFilesMenu()
    {
        var recent = _settings.Settings.RecentFiles;
        RecentFilesMenu.Items.Clear();

        if (recent.Count == 0)
        {
            RecentFilesMenu.Items.Add(new MenuItem { Header = "(none yet)", IsEnabled = false });
            return;
        }

        foreach (string path in recent)
        {
            var item = new MenuItem { Header = System.IO.Path.GetFileName(path), Tag = path };
            ToolTip.SetTip(item, path);
            item.Click += OnRecentFile;
            RecentFilesMenu.Items.Add(item);
        }

        RecentFilesMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear Recent Files" };
        clear.Click += (_, _) =>
        {
            _settings.Settings.RecentFiles.Clear();
            _settings.Save();
            RebuildRecentFilesMenu();
        };
        RecentFilesMenu.Items.Add(clear);
    }

    // ---- plugins ------------------------------------------------------------
    //
    // Purely additive: built-in effects/tools stay on their existing hardcoded switches
    // (OnAdjust/OnEffect, SelectTool) unchanged. Plugin contributions live in EffectRegistry/
    // ToolRegistry (KawaPaint.Engine.Plugins) and are surfaced here, mirroring how
    // RebuildLayoutPresetsMenu inserts dynamic entries into an otherwise-static menu shell.

    private void OnPluginRegistryChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RebuildPluginsMenu);

    private void RebuildPluginsMenu()
    {
        PluginEffectsMenu.Items.Clear();
        var categories = new System.Collections.Generic.Dictionary<string, MenuItem>();

        foreach (var d in KawaPaint.Engine.Plugins.EffectRegistry.All)
        {
            var item = new MenuItem { Header = d.DisplayName + "…", Tag = d };
            item.Click += (_, _) => OnPluginEffect(d);

            if (d.Category is null)
            {
                PluginEffectsMenu.Items.Add(item);
                continue;
            }

            if (!categories.TryGetValue(d.Category, out var submenu))
            {
                submenu = new MenuItem { Header = d.Category };
                categories[d.Category] = submenu;
                PluginEffectsMenu.Items.Add(submenu);
            }
            submenu.Items.Add(item);
        }
        PluginEffectsMenu.IsVisible = KawaPaint.Engine.Plugins.EffectRegistry.All.Count > 0;

        PluginToolsMenu.Items.Clear();
        foreach (var d in KawaPaint.Engine.Plugins.ToolRegistry.All)
        {
            var item = new MenuItem { Header = d.DisplayName };
            item.Click += (_, _) => SelectTool("plugin:" + d.Id);
            PluginToolsMenu.Items.Add(item);
        }
        PluginToolsMenu.IsVisible = KawaPaint.Engine.Plugins.ToolRegistry.All.Count > 0;

        RebuildPluginToolButtons();
    }

    private void OnPluginEffect(KawaPaint.Engine.Plugins.PluginEffectDescriptor descriptor)
    {
        if (Canvas.ActiveLayer is null) return;
        RecordSkipped("plugin effect '" + descriptor.DisplayName + "'");
        if (OwnerWindow is not { } owner)
        {
            StatusText.Text = "Plugin effects aren't available in the browser build yet";
            return;
        }

        new PluginEffectDialog(Canvas, descriptor).ShowDialog(owner);
    }

    private async void OnPreferences(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (OwnerWindow is not { } owner)
        {
            StatusText.Text = "Preferences aren't available in the browser build yet";
            return;
        }

        await new SettingsDialog(_settings).ShowDialog(owner);

        // AutosaveService and ConfigGitTracker re-read themselves off SettingsService.Changed, but
        // the undo stack's limits are pushed to it rather than pulled, so re-push them here.
        ApplyHistorySettings();
        ApplyDrawingSettings();
    }

    private async void OnManagePlugins(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (OwnerWindow is not { } owner) return;
        await new PluginManagerDialog(_settings, RebuildPluginsMenu).ShowDialog(owner);
    }

    private async void OnRecentFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path }) return;
        if (!await ConfirmDiscardAsync()) return;

        if (!System.IO.File.Exists(path))
        {
            StatusText.Text = "File not found: " + path;
            _settings.RemoveRecentFile(path);
            RebuildRecentFilesMenu();
            return;
        }

        try
        {
            var file = await StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            if (file is null) { StatusText.Text = "Couldn't reopen: " + path; return; }

            Document doc;
            await using (var stream = await file.OpenReadAsync())
                doc = DocumentFile.Load(stream);
            Canvas.SetDocument(doc);
            SetClean(file);
            StatusText.Text = $"{file.Name} - {doc.LayerCount} layer(s)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Open failed: " + ex.Message;
        }
    }

    // ---- saveable layout presets -------------------------------------------
    //
    // A named WorkspaceLayout snapshot, switchable from the View ▸ Layout menu. Local settings
    // file by default (see AppSettings.Workspace.Layouts); a git-backed alternative can read the
    // same structure later without changing this code.

    private const int LayoutPresetsMenuStaticItemCount = 4;   // Save As, Rename, Delete, Separator

    private void RebuildLayoutPresetsMenu()
    {
        while (LayoutPresetsMenu.Items.Count > LayoutPresetsMenuStaticItemCount)
            LayoutPresetsMenu.Items.RemoveAt(LayoutPresetsMenu.Items.Count - 1);

        var workspace = _settings.Settings.Workspace;
        foreach (string name in workspace.Layouts.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var item = new MenuItem
            {
                Header = name,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "LayoutPreset",
                IsChecked = name == workspace.ActiveLayout
            };
            item.Click += (_, _) => SwitchLayoutPreset(name);
            LayoutPresetsMenu.Items.Add(item);
        }
    }

    private void SwitchLayoutPreset(string name)
    {
        var workspace = _settings.Settings.Workspace;
        if (!workspace.Layouts.TryGetValue(name, out var layout) || name == workspace.ActiveLayout) return;

        workspace.ActiveLayout = name;
        _panels.SetLayout(layout);
        RefreshPanelToggleButtons();
        _settings.Save();
        RebuildLayoutPresetsMenu();
        StatusText.Text = "Switched to layout \"" + name + "\"";
    }

    private async void OnSaveLayoutPreset(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var owner = OwnerWindow;
        if (owner is null) return;   // no dialog host under the browser single-view build

        var workspace = _settings.Settings.Workspace;
        var dlg = new PromptDialog("Save Layout As", workspace.ActiveLayout);
        if (!await dlg.ShowDialog<bool>(owner)) return;

        string name = dlg.ResultText.Trim();
        if (name.Length == 0) return;

        workspace.Layouts[name] = _panels.Layout.Clone();
        workspace.ActiveLayout = name;
        _settings.Save();
        RebuildLayoutPresetsMenu();
        StatusText.Text = "Saved layout \"" + name + "\"";
    }

    private async void OnRenameLayoutPreset(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var owner = OwnerWindow;
        if (owner is null) return;

        var workspace = _settings.Settings.Workspace;
        string oldName = workspace.ActiveLayout;
        var dlg = new PromptDialog("Rename Layout", oldName);
        if (!await dlg.ShowDialog<bool>(owner)) return;

        string newName = dlg.ResultText.Trim();
        if (newName.Length == 0 || newName == oldName) return;
        if (!workspace.Layouts.TryGetValue(oldName, out var layout)) return;

        workspace.Layouts.Remove(oldName);
        workspace.Layouts[newName] = layout;
        workspace.ActiveLayout = newName;
        _settings.Save();
        RebuildLayoutPresetsMenu();
        StatusText.Text = "Renamed layout to \"" + newName + "\"";
    }

    private async void OnDeleteLayoutPreset(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var workspace = _settings.Settings.Workspace;
        if (workspace.Layouts.Count <= 1)
        {
            StatusText.Text = "Can't delete the only layout";
            return;
        }

        // The menu item has always promised a prompt with its ellipsis; until now it deleted a
        // saved arrangement outright, with nothing to undo it. Via ConfirmAsync so the browser host
        // gets the prompt too - branching on OwnerWindow here used to skip it entirely there.
        if (!await ConfirmAsync("Delete Layout",
                $"Delete the layout \"{workspace.ActiveLayout}\"? This can't be undone.",
                confirmLabel: "Delete"))
            return;

        string removed = workspace.ActiveLayout;
        workspace.Layouts.Remove(removed);

        string next = workspace.Layouts.ContainsKey("Default") ? "Default" : workspace.Layouts.Keys.First();
        workspace.ActiveLayout = next;
        _panels.SetLayout(workspace.Layouts[next]);
        RefreshPanelToggleButtons();
        _settings.Save();
        RebuildLayoutPresetsMenu();
        StatusText.Text = "Deleted layout \"" + removed + "\"";
    }

    // No-op under the browser single-view host (no desktop window to close).
    private void OnExit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OwnerWindow?.Close();

    private async void OnAbout(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (OwnerWindow is not { } owner) return;
        await new AboutDialog().ShowDialog(owner);
    }

    // ---- toolbar ----------------------------------------------------------

    private void OnColor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hex) return;
        if (!ColorBgra.TryParseHexString(hex, out var color)) return;
        // These swatches are labelled "Fg", so they always set the foreground - regardless of
        // which swatch the color wheel currently edits.
        SetForeground(color);
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
        UpdateValueCursorColor();
    }

    /// <summary>Keeps the Value slider's track and thumb following the wheel's hue/saturation.
    /// The slider only redraws its own gradient from its own HsvColor, which nothing else keeps
    /// current as the wheel moves, so both the bar and the (separately brushed, see
    /// ValueSliderCursorBrush) thumb would otherwise go stale until the panel next resyncs. The
    /// thumb brush uses _value (its own position along the bar) rather than full brightness, so
    /// it shows the color actually at that point on the gradient.</summary>
    private void UpdateValueCursorColor()
    {
        var hsv = ColorWheel.HsvColor;

        _suppressColor = true;
        ValueSlider.HsvColor = new HsvColor(1, hsv.H, hsv.S, _value);
        _suppressColor = false;

        if (_valueCursorBrush is not null)
            _valueCursorBrush.Color = new HsvColor(1, hsv.H, hsv.S, _value).ToRgb();
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
        RecordAction("color.swap");
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
        UpdateValueCursorColor();
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
            string hex = color.ToDisplayHexString();
            ToolTip.SetTip(swatch, string.IsNullOrEmpty(e.Name) ? hex : $"{e.Name}  ({hex})");
            swatch.Click += (_, _) => SetForeground(color);

            var menu = new ContextMenu();
            var asBg = new MenuItem { Header = "Set as Background" };
            asBg.Click += (_, _) => SetBackground(color);
            var rename = new MenuItem { Header = "Rename…" };
            rename.Click += async (_, _) =>
            {
                string? name;
                if (OwnerWindow is { } owner)
                {
                    var dlg = new PromptDialog("Name color", e.Name ?? "");
                    name = await dlg.ShowDialog<bool>(owner) ? dlg.ResultText : null;
                }
                else name = await ShowCanvasPromptAsync("Name color", e.Name ?? "");
                if (name is not null) { e.Name = name.Trim(); PersistPalette(); BuildPaletteStrip(); }
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
        RecordColor(Core.Demo.DemoColorSlot.Foreground, c);
        Canvas.BrushColor = c;
        if (!_editingSecondary) SyncWheelToActiveColor();
        RefreshSwatches();
    }

    private void SetBackground(ColorBgra c)
    {
        RecordColor(Core.Demo.DemoColorSlot.Background, c);
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

        // Deliberately NOT Palette.LoadOrDefault: that swallows a parse failure and hands back the
        // 12 built-in colours, which PersistPalette would then write straight over the user's own
        // palette.kwpal - silent data loss, reported as "Palette loaded". Load strictly instead and
        // leave the current palette untouched when the file turns out not to be one.
        Palette loaded;
        try
        {
            await using var stream = await file.OpenReadAsync();
            loaded = Palette.Load(stream);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't load {file.Name}: {ex.Message}";
            return;
        }

        _palette = loaded;
        PersistPalette();
        BuildPaletteStrip();
        StatusText.Text = $"Palette loaded - {_palette.Colors.Count} colour(s)";
    }

    private void OnTool(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) SelectTool(tag);
    }

    // Grouped for the toolbar: additive/paint tools, then selection tools, then click-drag shapes
    // - a divider is drawn between each group in BuildToolPalette.
    private static readonly (string Key, string Name, string Shortcut)[][] ToolGroups =
    {
        new (string Key, string Name, string Shortcut)[]
        {
            ("Pencil", "Pencil", "P"), ("Brush", "Paintbrush", "B"), ("Eraser", "Eraser", "E"), ("Fill", "Paint Bucket", "F"),
            ("Pick", "Color Picker", "K"), ("Gradient", "Gradient", "G"), ("Clone", "Clone Stamp", "C"),
            ("Recolor", "Recolor", "N"), ("Text", "Text", "T"), ("DynamicText", "Dynamic Text / CSV Zone", "")
        },
        new (string Key, string Name, string Shortcut)[]
        {
            ("Move", "Move", "M"), ("RectSel", "Rectangle Select", "S"),
            ("EllipseSel", "Ellipse Select", "S S"), ("Lasso", "Lasso Select", "S S S"),
            ("Wand", "Magic Wand", "S S S S")
        },
        new (string Key, string Name, string Shortcut)[]
        {
            ("Line", "Line", "L"), ("Rect", "Rectangle", "R"), ("Ellipse", "Ellipse", "O"),
            ("RoundRect", "Rounded Rectangle", "U"), ("Freeform", "Freeform Shape", "D"),
            ("Star", "Star", "H"), ("Arrow", "Arrow", "A")
        }
    };

    private readonly System.Collections.Generic.List<ToggleButton> _toolButtons = new();
    private string _currentToolTag = "Pencil";

    private void BuildToolPalette()
    {
        // ToolPalette is a narrow vertical StackPanel (the side panel is only 70px wide, so a
        // single-row layout isn't an option): each group gets its own WrapPanel so its buttons
        // still flow horizontally and wrap within that width, and a horizontal rule spanning the
        // panel separates one group's block of rows from the next.
        for (int g = 0; g < ToolGroups.Length; g++)
        {
            var group = new WrapPanel { Orientation = Orientation.Horizontal };

            foreach (var (key, name, sc) in ToolGroups[g])
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
                group.Children.Add(btn);

                // Crop to Selection is a one-shot command, not a persistent tool, so it's a plain
                // Button rather than one of the ToggleButtons above. Grouped right after the
                // selection tools since it acts on whatever they selected.
                if (key == "Wand")
                {
                    var cropBtn = new Button
                    {
                        Content = Icons.Create("Crop"),
                        Width = 28,
                        Height = 28,
                        Padding = new Thickness(3),
                        Margin = new Thickness(1)
                    };
                    ToolTip.SetTip(cropBtn, "Crop to Selection");
                    cropBtn.Click += OnCropToSelection;
                    group.Children.Add(cropBtn);
                }
            }

            ToolPalette.Children.Add(group);

            if (g < ToolGroups.Length - 1)
                ToolPalette.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(2, 4),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = Brushes.DimGray
                });
        }
    }

    /// <summary>Appends (or, on a reload, replaces) one more WrapPanel group for
    /// ToolRegistry-contributed tools, same ToggleButton/Icons.Create/Tag wiring as every built-in
    /// group in BuildToolPalette above - additive, the static groups above are untouched.</summary>
    private void RebuildPluginToolButtons()
    {
        const string marker = "PluginToolGroup";

        for (int i = ToolPalette.Children.Count - 1; i >= 0; i--)
        {
            if (ToolPalette.Children[i] is not Control c || (c.Tag as string) != marker) continue;

            if (c is WrapPanel oldGroup)
                foreach (var child in oldGroup.Children)
                    if (child is ToggleButton oldBtn) _toolButtons.Remove(oldBtn);

            ToolPalette.Children.RemoveAt(i);
        }

        var tools = KawaPaint.Engine.Plugins.ToolRegistry.All;
        if (tools.Count == 0) return;

        ToolPalette.Children.Add(new Border
        {
            Tag = marker,
            Height = 1,
            Margin = new Thickness(2, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.DimGray
        });

        var group = new WrapPanel { Tag = marker, Orientation = Orientation.Horizontal };
        foreach (var d in tools)
        {
            string key = "plugin:" + d.Id;
            var btn = new ToggleButton
            {
                Content = Icons.Create("Plugin"),
                Width = 28,
                Height = 28,
                Padding = new Thickness(3),
                Margin = new Thickness(1),
                Tag = key
            };
            ToolTip.SetTip(btn, d.DisplayName);
            btn.Click += (_, _) => SelectTool(key);
            _toolButtons.Add(btn);
            group.Children.Add(btn);
        }
        ToolPalette.Children.Add(group);
    }

    private void SelectTool(string tag)
    {
        RecordTool(tag);
        _currentToolTag = tag;
        foreach (var b in _toolButtons)
            b.IsChecked = (b.Tag as string) == tag;

        // Checked before the built-in switch (whose own fallback is PencilTool, not an error) so a
        // "plugin:<id>" tag can't silently resolve to Pencil.
        ITool tool = tag.StartsWith("plugin:") && KawaPaint.Engine.Plugins.ToolRegistry.TryGet(tag[7..], out var pluginTool)
            ? new KawaPaint.App.Core.Plugins.PluginToolAdapter(pluginTool.Create())
            : tag switch
        {
            "Brush" => new PaintbrushTool(),
            "Eraser" => new EraserTool(),
            "Fill" => new PaintBucketTool(),
            "Pick" => new ColorPickerTool(),
            "Line" => new LineTool(),
            "Rect" => new RectangleTool(),
            "Ellipse" => new EllipseTool(),
            "Gradient" => new GradientTool(),
            "Text" => new TextTool(),
            "DynamicText" => new DynamicTextTool(),
            "Move" => new MoveTool(),
            "RectSel" => new RectSelectTool(),
            "EllipseSel" => new EllipseSelectTool(),
            "Lasso" => new LassoSelectTool(),
            "Wand" => new MagicWandTool(),
            "Clone" => new CloneStampTool(),
            "Recolor" => new RecolorTool(),
            "RoundRect" => new RoundedRectangleTool(),
            "Freeform" => new FreeformShapeTool(),
            "Star" => new StarTool(),
            "Arrow" => new ArrowTool(),
            _ => new PencilTool()
        };
        Canvas.CurrentTool = tool;
        UpdateToolOptions(tag);
        StatusText.Text = "Tool: " + tool.Name;
    }

    /// <summary>Greys out toolbar options the active tool ignores.</summary>
    private void UpdateToolOptions(string tag)
    {
        SizeGroup.IsEnabled = tag is "Pencil" or "Brush" or "Eraser" or "Line" or "Rect" or "Ellipse" or "Clone" or "Recolor" or "RoundRect" or "Freeform" or "Star" or "Arrow";
        // Hardness is the paintbrush's alone; and the paintbrush is always antialiased by
        // construction, so the AA checkbox has nothing to say about it.
        BrushGroup.IsEnabled = tag is "Brush";
        // Also on for the shape-based select tools: AA governs their edge coverage now, and the
        // checkbox would otherwise be greyed out at exactly the moment it applies. Magic Wand is
        // excluded - it builds its mask from colour similarity pixel by pixel, with no edge to
        // antialias. FillShapesCheck is gated separately below, so it stays off for them.
        ShapeGroup.IsEnabled = tag is "Pencil" or "Line" or "Rect" or "Ellipse" or "Clone" or "Recolor" or "RoundRect" or "Freeform" or "Star" or "Arrow"
            or "RectSel" or "EllipseSel" or "Lasso";
        FillShapesCheck.IsEnabled = tag is "Rect" or "Ellipse" or "RoundRect" or "Freeform" or "Star" or "Arrow";
        BucketGroup.IsEnabled = tag is "Fill" or "Wand" or "Recolor";
        SelectGroup.IsEnabled = tag is "RectSel" or "EllipseSel" or "Lasso" or "Wand";
    }

    private void OnSelectionCombineMode(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }) return;
        if (!Enum.TryParse<SelectionCombineMode>(tag, out var mode)) return;
        ApplySelectionCombineMode(mode);
    }

    /// <summary>Sets the combine mode and re-syncs the four buttons. Split out from the click
    /// handler so demo playback can set it without a ToggleButton to hang the Tag off.</summary>
    private void ApplySelectionCombineMode(SelectionCombineMode mode)
    {
        RecordParam(Core.Demo.DemoParam.SelectionCombineMode, (int)mode);
        Canvas.SelectionCombineMode = mode;

        // These four act as a radio group; Avalonia's ToggleButton has no built-in GroupName
        // radio behaviour (that's MenuItem-only), so it's kept in sync by hand here.
        CombineReplaceBtn.IsChecked = mode == SelectionCombineMode.Replace;
        CombineAddBtn.IsChecked = mode == SelectionCombineMode.Add;
        CombineSubtractBtn.IsChecked = mode == SelectionCombineMode.Subtract;
        CombineIntersectBtn.IsChecked = mode == SelectionCombineMode.Intersect;
    }

    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        // Avalonia's MenuItem.InputGesture only *renders* the shortcut text - it never handles the
        // key - so every accelerator in the app is dispatched from here through the registry.
        bool inTextBox = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;
        if (_commands.HandleKey(e, inTextBox)) e.Handled = true;
    }

    private async void OnTextRequested(int x, int y)
    {
        var layer = Canvas.ActiveLayer;
        if (layer is null) return;

        // The typed string is dialog input, so it isn't in the demo file. Opening a modal prompt
        // in the middle of a replay would also stall the player's clock behind it.
        if (IsPlayingDemo)
        {
            StatusText.Text = "Demo: skipped Text (the typed string isn't recorded)";
            return;
        }
        RecordSkipped("Text");

        string text;
        int size;
        if (OwnerWindow is { } owner)
        {
            var dlg = new TextDialog();
            bool ok = await dlg.ShowDialog<bool>(owner);
            if (!ok) return;
            (text, size) = (dlg.ResultText, dlg.ResultSize);
        }
        else
        {
            var values = await ShowCanvasTextAsync();
            if (values is null) return;
            (text, size) = (values.Text, values.Size);
        }
        if (string.IsNullOrEmpty(text)) return;

        var snapshot = layer.Surface.Clone();
        TextOps.DrawText(layer.Surface, text, x, y, size, Canvas.BrushColor);
        if (Canvas.Selection is { IsActive: true }) Canvas.Selection.Clip(layer.Surface, snapshot);
        Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, "Text"));
        Canvas.RenderComposite();
        Canvas.InvalidateVisual();
        Canvas.NotifyLayersChanged();
        _scriptRecorder.NoteAction("text.draw", new double[] { x, y, size, Canvas.BrushColor.Bgra },
            new[] { text });
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

    /// <summary>Sets the brush/outline size, clamped to range, and syncs SizeBox to match -
    /// selecting the matching preset if there is one, and always updating the editable text.</summary>
    private void ApplyBrushSize(int size)
    {
        size = Math.Clamp(size, MinBrushSize, MaxBrushSize);
        RecordParam(Core.Demo.DemoParam.BrushSize, size);
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

    /// <summary>Lets the wheel nudge the size while hovering SizeBox, Shift for a bigger jump -
    /// independent of the box's own built-in wheel behavior (see the handledEventsToo hookup).</summary>
    private void OnSizeWheel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        int step = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? 10 : 1;
        int delta = Math.Sign(e.Delta.Y) * step;
        if (delta == 0) return;
        ApplyBrushSize(Canvas.BrushWidth + delta);
        e.Handled = true;
    }

    private bool _suppressHardness;   // guards programmatic updates to HardnessBox

    /// <summary>Sets the paintbrush hardness from a whole-percent value, clamped to range, and
    /// syncs HardnessBox to match - same shape as ApplyBrushSize/ApplyTolerance above.</summary>
    private void ApplyBrushHardness(int percent)
    {
        percent = Math.Clamp(percent, MinHardness, MaxHardness);
        RecordParam(Core.Demo.DemoParam.Hardness, percent);
        if (Canvas is not null) Canvas.BrushHardness = percent / 100.0;
        if (HardnessBox is null) return;

        _suppressHardness = true;
        HardnessBox.SelectedItem = Array.IndexOf(HardnessPresets, percent) >= 0 ? percent : null;
        HardnessBox.Text = percent.ToString();
        _suppressHardness = false;
    }

    private int CurrentHardnessPercent => (int)Math.Round(Canvas.BrushHardness * 100);

    private void OnHardnessPresetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressHardness) return;
        if (HardnessBox.SelectedItem is int percent) ApplyBrushHardness(percent);
    }

    private void OnHardnessKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        CommitHardnessText();
        e.Handled = true;
    }

    private void OnHardnessTextCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => CommitHardnessText();

    private void CommitHardnessText()
    {
        if (_suppressHardness || HardnessBox is null) return;
        ApplyBrushHardness(int.TryParse((HardnessBox.Text ?? "").Trim(), out int percent)
            ? percent : CurrentHardnessPercent);
    }

    private void OnHardnessWheel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        int step = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? 10 : 1;
        int delta = Math.Sign(e.Delta.Y) * step;
        if (delta == 0) return;
        ApplyBrushHardness(CurrentHardnessPercent + delta);
        e.Handled = true;
    }

    private void OnAntialias(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas is null || AntialiasCheck is null) return;
        Canvas.Antialias = AntialiasCheck.IsChecked ?? true;
        RecordParam(Core.Demo.DemoParam.Antialias, Canvas.Antialias);
    }

    private void OnFillShapes(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas is null || FillShapesCheck is null) return;
        Canvas.FillShapes = FillShapesCheck.IsChecked ?? false;
        RecordParam(Core.Demo.DemoParam.FillShapes, Canvas.FillShapes);
    }

    private void OnGlobalFill(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas is null || GlobalFillCheck is null) return;
        Canvas.GlobalFill = GlobalFillCheck.IsChecked ?? false;
        RecordParam(Core.Demo.DemoParam.GlobalFill, Canvas.GlobalFill);
    }

    private bool _suppressTolerance;   // guards programmatic updates to ToleranceBox

    /// <summary>Sets the fill tolerance, clamped to range, and syncs ToleranceBox to match -
    /// selecting the matching preset if there is one, and always updating the editable text.</summary>
    private void ApplyTolerance(int tol)
    {
        tol = Math.Clamp(tol, MinTolerance, MaxTolerance);
        RecordParam(Core.Demo.DemoParam.Tolerance, tol);
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
                Child = new Image { Source = ThumbnailFor(layer), Stretch = Stretch.Uniform }
            };
            var capturedLayer = layer;
            check.IsCheckedChanged += (_, _) =>
            {
                if (_suppress) return;
                bool now = check.IsChecked ?? true;
                if (Canvas.Document is { } d)
                    RecordAction($"layer.visible.{d.IndexOf(capturedLayer)}.{(now ? 1 : 0)}");
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
                string? name;
                if (OwnerWindow is { } owner)
                {
                    var dlg = new PromptDialog("Rename layer", capturedLayer.Name);
                    name = await dlg.ShowDialog<bool>(owner) ? dlg.ResultText : null;
                }
                else name = await ShowCanvasPromptAsync("Rename layer", capturedLayer.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    capturedLayer.Name = name.Trim();
                    _demoRecorder.NoteSkipped("Layer rename");
                    _scriptRecorder.NoteAction("layer.rename", Array.Empty<double>(), new[] { capturedLayer.Name });
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

        PruneThumbnails(doc);
        _suppress = false;
    }

    // ---- layer thumbnails ---------------------------------------------------
    //
    // RebuildLayerPanel runs on every DocumentChanged, and most of those change no layer pixels at
    // all (selecting a row, finishing a drag-reorder). Each rebuild used to downsample every layer
    // into a brand-new WriteableBitmap and drop the previous one on the floor for the finalizer to
    // collect - a native allocation per layer per event. Now each layer keeps one bitmap, redrawn
    // in place only when SurfaceView.CompositeVersion says the pixels may have moved.

    private const int ThumbWidth = 38;
    private const int ThumbHeight = 28;

    private readonly System.Collections.Generic.Dictionary<Layer, (WriteableBitmap Bitmap, int Version)> _thumbnails = new();

    private WriteableBitmap ThumbnailFor(Layer layer)
    {
        int version = Canvas.CompositeVersion;

        if (_thumbnails.TryGetValue(layer, out var cached))
        {
            if (cached.Version == version) return cached.Bitmap;

            // Same layer, same target size: repaint the bitmap we already have.
            var size = ThumbnailSize(layer.Surface);
            if (cached.Bitmap.PixelSize == size)
            {
                RenderThumbnail(layer.Surface, cached.Bitmap, size);
                _thumbnails[layer] = (cached.Bitmap, version);
                return cached.Bitmap;
            }

            // The layer was resized under us (a canvas-level op), so the old bitmap is the wrong
            // shape and has to go.
            DisposeLater(cached.Bitmap);
        }

        var created = MakeThumbnail(layer.Surface);
        _thumbnails[layer] = (created, version);
        return created;
    }

    /// <summary>Drops cached thumbnails for layers the document no longer holds. Called from
    /// RebuildLayerPanel, which is the only thing that puts entries in.</summary>
    private void PruneThumbnails(Document doc)
    {
        System.Collections.Generic.List<Layer>? gone = null;
        foreach (var layer in _thumbnails.Keys)
            if (doc.IndexOf(layer) < 0) (gone ??= new()).Add(layer);

        if (gone is null) return;
        foreach (var layer in gone)
        {
            DisposeLater(_thumbnails[layer].Bitmap);
            _thumbnails.Remove(layer);
        }
    }

    /// <summary>Frees a thumbnail's native buffer on a later dispatcher pass rather than inline.
    /// The Image showing it has only just been detached by LayerList.Items.Clear(), and disposing
    /// a bitmap the compositor may still hold for the frame in flight is not safe.</summary>
    private static void DisposeLater(WriteableBitmap bitmap) =>
        Dispatcher.UIThread.Post(bitmap.Dispose, DispatcherPriority.Background);

    private static PixelSize ThumbnailSize(Surface s)
    {
        double scale = Math.Min((double)ThumbWidth / s.Width, (double)ThumbHeight / s.Height);
        return new PixelSize(Math.Max(1, (int)(s.Width * scale)), Math.Max(1, (int)(s.Height * scale)));
    }

    private static WriteableBitmap MakeThumbnail(Surface s)
    {
        var size = ThumbnailSize(s);
        var wb = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        RenderThumbnail(s, wb, size);
        return wb;
    }

    private static unsafe void RenderThumbnail(Surface s, WriteableBitmap target, PixelSize size)
    {
        using var small = s.Resized(size.Width, size.Height);
        using var fb = target.Lock();
        int rowBytes = size.Width * 4;
        byte* dst = (byte*)fb.Address;
        for (int y = 0; y < size.Height; y++)
            System.Buffer.MemoryCopy(small.GetRowPointer(y), dst + (long)y * fb.RowBytes, fb.RowBytes, rowBytes);
    }

    // ---- history panel ------------------------------------------------------
    //
    // A registered panel like Tools or Layers (see BuildPanelManager). Row 0 is a synthetic
    // "Start" entry for position 0 (nothing applied yet) - HistoryStack.Steps() only enumerates
    // actual edits, but the empty state is a valid, clickable position too.

    private void RebuildHistoryPanel()
    {
        var history = Canvas.History;
        _suppressHistory = true;

        var steps = history.Steps();
        if (HistoryList.Items.Count == 0)
            HistoryList.Items.Add(new ListBoxItem { Content = "Start", Tag = 0 });

        // Keep the unchanged prefix of controls. Undo/redo now updates opacity/selection in place;
        // a push appends one row; truncating a redo branch removes only that tail.
        int matching = 0;
        while (matching < steps.Count && matching + 1 < HistoryList.Items.Count)
        {
            if (HistoryList.Items[matching + 1] is not ListBoxItem item ||
                !Equals(item.Content, $"{matching + 1}. {steps[matching].Name}")) break;
            matching++;
        }

        while (HistoryList.Items.Count > matching + 1)
            HistoryList.Items.RemoveAt(HistoryList.Items.Count - 1);

        for (int i = matching; i < steps.Count; i++)
            HistoryList.Items.Add(CreateHistoryRow(i + 1, steps[i].Name));

        for (int i = 0; i < steps.Count; i++)
            if (HistoryList.Items[i + 1] is ListBoxItem item)
                item.Opacity = steps[i].IsApplied ? 1 : 0.45;

        HistoryList.SelectedIndex = history.Position;
        if (HistoryList.SelectedItem is { } selected) HistoryList.ScrollIntoView(selected);

        double residentMb = history.ResidentBytes / (1024.0 * 1024.0);
        HistoryUsageText.Text = history.Count == 1
            ? $"1 step · {residentMb:0.#} MB"
            : $"{history.Count} steps · {residentMb:0.#} MB";

        _suppressHistory = false;
    }

    /// <summary>
    /// One step row, carrying its 1-based caret position in Tag (row N is the state after step
    /// N-1). The context menu is attached per row rather than to the ListBox as a whole, so the
    /// row the menu acts on is fixed at creation and never has to be re-derived from the selection.
    ///
    /// Note what a right-click here does, confirmed by driving the app rather than assumed: Avalonia
    /// selects the row on right-press, and this panel's selection *is* the undo caret, so opening
    /// the menu jumps the document to that step. Left as-is deliberately. The two cannot be
    /// separated while the selection means the caret, and the alternative is worse: a menu acting on
    /// row 3 while the highlight still says row 5. It is also only navigation - cancelling leaves
    /// the later steps intact and redoable, one click away on their own rows.
    /// </summary>
    private ListBoxItem CreateHistoryRow(int position, string name)
    {
        var row = new ListBoxItem { Content = $"{position}. {name}", Tag = position };

        var truncate = new MenuItem { Header = "Delete From Here…" };
        truncate.Click += (_, _) => _ = TruncateHistoryAsync(position);
        ToolTip.SetTip(truncate, "Drop this step and every step after it");

        var menu = new ContextMenu();
        menu.Items.Add(truncate);
        row.ContextMenu = menu;
        return row;
    }

    /// <summary>
    /// Drops a step and everything after it. Steps are snapshots rather than a replayable command
    /// log, so a step in the middle cannot be plucked out on its own - see HistoryStack.TruncateFrom.
    /// </summary>
    private async Task TruncateHistoryAsync(int position)
    {
        var history = Canvas.History;
        int index = position - 1;   // Tag is a caret position; TruncateFrom takes a step index
        if (index < 0 || index >= history.Count) return;

        int dropped = history.Count - index;
        // Numbered, matching the row label: step names repeat constantly (five pencil strokes are
        // five steps all called "Pencil"), so the name alone does not say which row is about to go.
        string step = $"step {position} \"{history.Steps()[index].Name}\"";
        string message = dropped == 1
            ? $"Delete {step}? The image goes back to how it was before it, and this can't be undone."
            : $"Delete {step} and the {dropped - 1} step(s) after it? The image goes back to how it " +
              "was before that step, and this can't be undone.";

        if (!await ConfirmAsync("Delete History Steps", message, confirmLabel: "Delete")) return;

        // The stack can have moved while the prompt was up (a demo replay, or a trim triggered by
        // the jump another handler made), so re-check rather than trusting the pre-prompt index.
        if (index >= history.Count) return;

        RecordAction("history.truncate." + position);
        Canvas.TruncateHistoryFrom(index);
        StatusText.Text = dropped == 1 ? "Deleted 1 history step" : $"Deleted {dropped} history steps";
    }

    private void OnHistorySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressHistory) return;
        if (HistoryList.SelectedItem is ListBoxItem { Tag: int position })
        {
            RecordAction("history.jump." + position);
            Canvas.JumpToHistory(position);
        }
    }

    private async void OnClearHistory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Discarding every undo step is as destructive as U11's layout delete and had no prompt
        // either. Nothing to confirm when the stack is already empty.
        if (Canvas.History.Count > 0 &&
            !await ConfirmAsync("Clear History",
                $"Discard all {Canvas.History.Count} undo step(s)? The image keeps its current pixels, " +
                "but nothing can be undone afterwards.", confirmLabel: "Clear"))
            return;

        RecordAction("history.clear");
        ClearHistoryCore();
    }

    /// <summary>The work behind Clear History, without the prompt - what a demo replay drives, so
    /// that replaying a recorded clear does not stop to ask the user a question.</summary>
    private void ClearHistoryCore()
    {
        Canvas.ClearHistory();
        StatusText.Text = "History cleared";
    }

    private void OnLayerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (LayerList.SelectedItem is ListBoxItem { Tag: Layer layer })
        {
            if (Canvas.Document is { } doc) RecordAction("layer.select." + doc.IndexOf(layer));
            Canvas.SetActiveLayer(layer);
        }
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

        RecordAction($"layer.reorder.{from}.{to}");
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

    // ---- clipboard ----------------------------------------------------------
    //
    // Cut/Copy act on the active layer within the current selection (or the whole canvas when
    // nothing is selected, matching Paint.NET). Copy Merged reads the flattened composite instead
    // of a single layer. Paste always alpha-composites rather than overwrites, so a pasted image's
    // transparent pixels don't blank out what's underneath.

    private (int X, int Y, int W, int H) SelectionOrCanvasBounds(Document doc)
        => Canvas.Selection is { IsActive: true } sel ? sel.GetBounds() : (0, 0, doc.Width, doc.Height);

    /// <summary>Crops to (x,y,w,h) and, if a selection is active, transparents whatever falls
    /// outside its shape - so copying a non-rectangular selection copies its actual outline.</summary>
    private static unsafe Surface ExtractRegion(Surface source, Selection? selection, int x, int y, int w, int h)
    {
        var region = source.Crop(x, y, w, h);
        if (selection is not { IsActive: true } sel) return region;

        for (int ry = 0; ry < h; ry++)
        {
            var row = (ColorBgra*)region.GetRowPointer(ry);
            for (int rx = 0; rx < w; rx++)
            {
                // Scaled by coverage rather than an all-or-nothing IsSelected test, so an
                // antialiased outline is copied with a soft edge instead of being re-hardened on
                // the way out. Unchanged for a binary mask: coverage is 0 or 255 there.
                byte coverage = sel.CoverageAt(x + rx, y + ry);
                if (coverage == 255) continue;
                row[rx] = coverage == 0
                    ? ColorBgra.Transparent
                    : ColorBgra.FromBgra(row[rx].B, row[rx].G, row[rx].R, (byte)(row[rx].A * coverage / 255));
            }
        }
        return region;
    }

    /// <summary>
    /// Fills the selection's bounds with <paramref name="color"/> and then clips back against the
    /// pre-edit snapshot. Going through <see cref="Selection.Clip"/> rather than testing IsSelected
    /// per pixel is what makes these commands honour an antialiased edge at all - IsSelected is
    /// all-or-nothing by design, so a per-pixel test re-hardens whatever coverage the mask holds.
    /// </summary>
    private static unsafe void FillSelectionBounds(Layer layer, Selection? selection, Surface snapshot,
        ColorBgra color, int x, int y, int w, int h)
    {
        for (int ry = 0; ry < h; ry++)
        {
            var row = (ColorBgra*)layer.Surface.GetRowPointer(y + ry);
            for (int rx = 0; rx < w; rx++) row[x + rx] = color;
        }
        if (selection is { IsActive: true } sel) sel.Clip(layer.Surface, snapshot, x, y, w, h);
    }

    private static Avalonia.Media.Imaging.Bitmap ToClipboardBitmap(Surface s)
    {
        return new Avalonia.Media.Imaging.Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul,
            s.Scan0, new PixelSize(s.Width, s.Height), new Vector(96, 96), s.Stride);
    }

    /// <summary>Copies the platform clipboard bitmap straight into KawaPaint's BGRA buffer. The
    /// clipboard API has already decoded its original external format by this point.</summary>
    private static Surface FromClipboardBitmap(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        var surface = new Surface(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        try
        {
            using var target = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var framebuffer = target.Lock()) bitmap.CopyPixels(framebuffer);
            using var source = target.Lock();
            unsafe
            {
                int rowBytes = surface.Stride;
                for (int y = 0; y < surface.Height; y++)
                    Buffer.MemoryCopy((byte*)source.Address + (long)y * source.RowBytes,
                        surface.GetRowPointer(y), rowBytes, rowBytes);
            }
            return surface;
        }
        catch
        {
            surface.Dispose();
            throw;
        }
    }

    private async void OnCopy(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordAction("edit.copy");
        var doc = Canvas.Document;
        var layer = Canvas.ActiveLayer;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (doc is null || layer is null || clipboard is null) return;

        var (x, y, w, h) = SelectionOrCanvasBounds(doc);
        if (w <= 0 || h <= 0) return;

        using var region = ExtractRegion(layer.Surface, Canvas.Selection, x, y, w, h);
        using var bitmap = ToClipboardBitmap(region);
        await clipboard.SetBitmapAsync(bitmap);
        StatusText.Text = "Copied";
    }

    private async void OnCopyMerged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordAction("edit.copyMerged");
        var doc = Canvas.Document;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (doc is null || clipboard is null) return;

        var (x, y, w, h) = SelectionOrCanvasBounds(doc);
        if (w <= 0 || h <= 0) return;

        using var flat = doc.Flatten();
        using var region = ExtractRegion(flat, Canvas.Selection, x, y, w, h);
        using var bitmap = ToClipboardBitmap(region);
        await clipboard.SetBitmapAsync(bitmap);
        StatusText.Text = "Copied (merged)";
    }

    private async void OnCut(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordAction("edit.cut");
        var doc = Canvas.Document;
        var layer = Canvas.ActiveLayer;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (doc is null || layer is null || clipboard is null) return;

        var (x, y, w, h) = SelectionOrCanvasBounds(doc);
        if (w <= 0 || h <= 0) return;

        using (var region = ExtractRegion(layer.Surface, Canvas.Selection, x, y, w, h))
        using (var bitmap = ToClipboardBitmap(region))
            await clipboard.SetBitmapAsync(bitmap);

        var snapshot = layer.Surface.Clone();
        unsafe { FillSelectionBounds(layer, Canvas.Selection, snapshot, ColorBgra.Transparent, x, y, w, h); }
        Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, "Cut"));
        RefreshDocument();
        StatusText.Text = "Cut";
    }

    /// <summary>
    /// If the pasted image doesn't fit the current canvas, asks whether to grow the canvas, scale
    /// the image down to fit, or paste as-is and let the overflow clip (today's default). Returns
    /// PasteAsIs when the image already fits, or when there's no window to host the dialog (the
    /// browser build), so paste degrades to the old clipping behavior there.
    /// </summary>
    private async Task<PastePlacement> ChoosePastePlacementAsync(int canvasWidth, int canvasHeight, int imageWidth, int imageHeight)
    {
        if (imageWidth <= canvasWidth && imageHeight <= canvasHeight) return PastePlacement.PasteAsIs;
        if (OwnerWindow is not { } owner)
        {
            var body = new TextBlock
            {
                Text = $"The pasted image ({imageWidth}×{imageHeight}) doesn't fit the canvas " +
                       $"({canvasWidth}×{canvasHeight}). What would you like to do?",
                TextWrapping = TextWrapping.Wrap
            };
            return await ShowCanvasChoiceAsync("Paste", body, PastePlacement.Cancel,
                new CanvasChoice<PastePlacement>("Cancel", PastePlacement.Cancel),
                new CanvasChoice<PastePlacement>("Paste As Is", PastePlacement.PasteAsIs),
                new CanvasChoice<PastePlacement>("Scale to Fit", PastePlacement.ScaleToFit),
                new CanvasChoice<PastePlacement>("Grow Canvas", PastePlacement.GrowCanvas, true));
        }
        return await new PastePlacementDialog(canvasWidth, canvasHeight, imageWidth, imageHeight).ShowDialog<PastePlacement>(owner);
    }

    private async void OnPaste(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordSkipped("Paste");
        var doc = Canvas.Document;
        var layer = Canvas.ActiveLayer;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (doc is null || layer is null || clipboard is null) return;

        using var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is null) { StatusText.Text = "Clipboard has no image"; return; }

        using var pasted = FromClipboardBitmap(bitmap);
        var placement = await ChoosePastePlacementAsync(doc.Width, doc.Height, pasted.Width, pasted.Height);
        if (placement == PastePlacement.Cancel) return;

        if (placement == PastePlacement.GrowCanvas)
        {
            int w = Math.Max(doc.Width, pasted.Width), h = Math.Max(doc.Height, pasted.Height);
            ApplyDocumentOp("Paste (Grow Canvas)", d =>
            {
                var grown = DocumentOps.ResizeCanvas(d, w, h, CanvasAnchor.TopLeft);
                SurfaceOps.CompositeOver(grown.Layers[^1].Surface, pasted, 0, 0);
                return grown;
            });
            StatusText.Text = $"Canvas grown to {w}×{h} and pasted {pasted.Width}×{pasted.Height}";
            return;
        }

        using Surface? scaled = placement == PastePlacement.ScaleToFit ? pasted.Resized(doc.Width, doc.Height) : null;
        var source = scaled ?? pasted;
        int originX = 0, originY = 0;
        if (scaled is null) (originX, originY, _, _) = SelectionOrCanvasBounds(doc);

        var snapshot = layer.Surface.Clone();
        SurfaceOps.CompositeOver(layer.Surface, source, originX, originY);
        Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, "Paste"));
        RefreshDocument();
        StatusText.Text = $"Pasted {pasted.Width}×{pasted.Height}";
    }

    private async void OnPasteIntoNewLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordSkipped("Paste Into New Layer");
        var doc = Canvas.Document;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (doc is null || clipboard is null) return;

        using var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is null) { StatusText.Text = "Clipboard has no image"; return; }

        using var pasted = FromClipboardBitmap(bitmap);
        var placement = await ChoosePastePlacementAsync(doc.Width, doc.Height, pasted.Width, pasted.Height);
        if (placement == PastePlacement.Cancel) return;

        if (placement == PastePlacement.GrowCanvas)
        {
            int w = Math.Max(doc.Width, pasted.Width), h = Math.Max(doc.Height, pasted.Height);
            ApplyDocumentOp("Paste (Grow Canvas)", d =>
            {
                var grown = DocumentOps.ResizeCanvas(d, w, h, CanvasAnchor.TopLeft);
                var newLayer = grown.AddLayer("Pasted");
                SurfaceOps.CompositeOver(newLayer.Surface, pasted, 0, 0);
                return grown;
            });
            StatusText.Text = $"Canvas grown to {w}×{h} and pasted {pasted.Width}×{pasted.Height} into a new layer";
            return;
        }

        using Surface? scaled = placement == PastePlacement.ScaleToFit ? pasted.Resized(doc.Width, doc.Height) : null;
        var source = scaled ?? pasted;

        var layer = doc.AddLayer("Pasted");
        SurfaceOps.CompositeOver(layer.Surface, source, 0, 0);
        Canvas.SetActiveLayer(layer);

        // Same detached-layer accounting as OnAddLayer below, and for the same reason: undo leaves
        // this memento the only owner of a full-size Layer, which the history budget would otherwise
        // read as costing nothing and never free. A paste is one of the largest layers a document
        // typically acquires, so skipping it here mattered more than at any of the four sites that
        // already had it.
        Canvas.History.Push(new DelegateMemento("Paste Into New Layer",
            undo: () => { doc.RemoveLayer(layer); Canvas.SetActiveLayer(doc.Layers[^1]); },
            redo: () => { doc.AddLayer(layer); Canvas.SetActiveLayer(layer); },
            approximateBytes: () => doc.IndexOf(layer) < 0 ? SurfaceBytes(layer.Surface) : 0,
            dispose: () => { if (doc.IndexOf(layer) < 0) layer.Dispose(); }));

        RefreshDocument();
        StatusText.Text = $"Pasted {pasted.Width}×{pasted.Height} into a new layer";
    }

    private async void OnImportLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null) return;
        RecordSkipped("Import Layer");

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import layer from image",
            AllowMultiple = false,
            FileTypeFilter = BuildOpenFilters()
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenReadAsync();
            using var imported = CodecRegistry.Decode(stream, file.Name);

            var layer = doc.AddLayer(System.IO.Path.GetFileNameWithoutExtension(file.Name));
            SurfaceOps.CompositeOver(layer.Surface, imported, 0, 0);
            Canvas.SetActiveLayer(layer);

            // Detached-layer accounting - see the identical note on Paste Into New Layer above.
            Canvas.History.Push(new DelegateMemento("Import Layer",
                undo: () => { doc.RemoveLayer(layer); Canvas.SetActiveLayer(doc.Layers[^1]); },
                redo: () => { doc.AddLayer(layer); Canvas.SetActiveLayer(layer); },
                approximateBytes: () => doc.IndexOf(layer) < 0 ? SurfaceBytes(layer.Surface) : 0,
                dispose: () => { if (doc.IndexOf(layer) < 0) layer.Dispose(); }));

            RefreshDocument();
            StatusText.Text = $"Imported {imported.Width}×{imported.Height} as a new layer";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Import failed: " + ex.Message;
        }
    }

    private async void OnPasteIntoNewImage(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RecordSkipped("Paste Into New Image");
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        using var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is null) { StatusText.Text = "Clipboard has no image"; return; }
        if (!await ConfirmDiscardAsync()) return;

        using var pasted = FromClipboardBitmap(bitmap);
        var doc = new Document(pasted.Width, pasted.Height);
        var layer = doc.AddLayer("Pasted");
        layer.Surface.CopyFrom(pasted);

        Canvas.SetDocument(doc);
        SetClean(null);
        StatusText.Text = $"New {pasted.Width}×{pasted.Height} document from clipboard";
    }

    private void OnFillSelection(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        var layer = Canvas.ActiveLayer;
        if (doc is null || layer is null) return;
        RecordAction("select.fill");

        var (x, y, w, h) = SelectionOrCanvasBounds(doc);
        if (w <= 0 || h <= 0) return;

        var snapshot = layer.Surface.Clone();
        unsafe { FillSelectionBounds(layer, Canvas.Selection, snapshot, Canvas.BrushColor, x, y, w, h); }
        Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, "Fill Selection"));
        RefreshDocument();
        StatusText.Text = "Filled";
    }

    private void OnEraseSelection(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        var layer = Canvas.ActiveLayer;
        if (doc is null || layer is null) return;
        RecordAction("select.erase");

        var (x, y, w, h) = SelectionOrCanvasBounds(doc);
        if (w <= 0 || h <= 0) return;

        var snapshot = layer.Surface.Clone();
        unsafe { FillSelectionBounds(layer, Canvas.Selection, snapshot, ColorBgra.Transparent, x, y, w, h); }
        Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, "Erase Selection"));
        RefreshDocument();
        StatusText.Text = "Erased";
    }

    /// <summary>Bytes a Surface holds - used to report the memory cost of a detached Layer to
    /// HistoryStack's budget (see the layer-lifecycle DelegateMementos below), the same way
    /// TileDeltaMemento/LayerSurfaceMemento already report theirs.</summary>
    private static long SurfaceBytes(Surface s) => (long)s.Stride * s.Height;

    private void OnAddLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        if (doc is null) return;
        RecordAction("layer.add");
        var layer = doc.AddLayer();
        Canvas.SetActiveLayer(layer);

        // approximateBytes/dispose are live queries against doc.IndexOf, not captured values: which
        // side (undo or redo) currently holds `layer` detached from the document flips with every
        // toggle, and Dispose() must never free a Surface the Document still owns.
        Canvas.History.Push(new DelegateMemento("Add Layer",
            undo: () => { doc.RemoveLayer(layer); Canvas.SetActiveLayer(doc.Layers[^1]); },
            redo: () => { doc.AddLayer(layer); Canvas.SetActiveLayer(layer); },
            approximateBytes: () => doc.IndexOf(layer) < 0 ? SurfaceBytes(layer.Surface) : 0,
            dispose: () => { if (doc.IndexOf(layer) < 0) layer.Dispose(); }));

        RefreshDocument();
    }

    private void OnDeleteLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        var active = Canvas.ActiveLayer;
        if (doc is null || active is null || doc.LayerCount <= 1) return;
        RecordAction("layer.delete");

        int idx = doc.IndexOf(active);
        doc.RemoveLayer(active);   // not disposed: undo may restore it
        Canvas.SetActiveLayer(doc.Layers[Math.Clamp(idx, 0, doc.LayerCount - 1)]);

        Canvas.History.Push(new DelegateMemento("Delete Layer",
            undo: () => { doc.InsertLayer(idx, active); Canvas.SetActiveLayer(active); },
            redo: () => { doc.RemoveLayer(active); Canvas.SetActiveLayer(doc.Layers[Math.Clamp(idx, 0, doc.LayerCount - 1)]); },
            approximateBytes: () => doc.IndexOf(active) < 0 ? SurfaceBytes(active.Surface) : 0,
            dispose: () => { if (doc.IndexOf(active) < 0) active.Dispose(); }));

        RefreshDocument();
    }

    private void OnDuplicateLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        var active = Canvas.ActiveLayer;
        if (doc is null || active is null) return;
        RecordAction("layer.duplicate");

        int idx = doc.IndexOf(active);
        var dup = active.Clone();
        doc.InsertLayer(idx + 1, dup);
        Canvas.SetActiveLayer(dup);

        Canvas.History.Push(new DelegateMemento("Duplicate Layer",
            undo: () => { doc.RemoveLayer(dup); Canvas.SetActiveLayer(active); },
            redo: () => { doc.InsertLayer(idx + 1, dup); Canvas.SetActiveLayer(dup); },
            approximateBytes: () => doc.IndexOf(dup) < 0 ? SurfaceBytes(dup.Surface) : 0,
            dispose: () => { if (doc.IndexOf(dup) < 0) dup.Dispose(); }));

        RefreshDocument();
    }

    private void OnMergeDown(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var doc = Canvas.Document;
        var active = Canvas.ActiveLayer;
        if (doc is null || active is null) return;
        int idx = doc.IndexOf(active);
        if (idx <= 0) { StatusText.Text = "Nothing below to merge into"; return; }
        RecordAction("layer.mergeDown");

        var below = doc.Layers[idx - 1];
        var belowBefore = below.Surface.Clone();
        LayerOps.MergeInto(below, active);
        doc.RemoveLayer(active);
        Canvas.SetActiveLayer(below);

        // belowBefore is owned by this memento for its whole lifetime (both directions - it's
        // needed for undo whether or not the step is currently applied), unlike `active`, whose
        // detached/attached state - and so whether it's this memento's to count/dispose - flips
        // with every toggle.
        Canvas.History.Push(new DelegateMemento("Merge Down",
            undo: () => { below.Surface.CopyFrom(belowBefore); doc.InsertLayer(idx, active); Canvas.SetActiveLayer(active); },
            redo: () => { LayerOps.MergeInto(below, active); doc.RemoveLayer(active); Canvas.SetActiveLayer(below); },
            approximateBytes: () => SurfaceBytes(belowBefore) + (doc.IndexOf(active) < 0 ? SurfaceBytes(active.Surface) : 0),
            dispose: () => { belowBefore.Dispose(); if (doc.IndexOf(active) < 0) active.Dispose(); }));

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
        RecordAction(delta > 0 ? "layer.up" : "layer.down");
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
            RecordAction("layer.blend." + mode);
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
        // First change of a gesture: remember the pre-edit value, and which layer it belongs to.
        // The commit can arrive after the active layer has moved on (LostFocus fires as the click
        // that selected another row lands), and applying A's undo entry to B is silently wrong.
        if (_opacityBefore is null)
        {
            _opacityBefore = Canvas.ActiveLayer.Opacity;
            _opacityLayer = Canvas.ActiveLayer;
        }
        Canvas.ActiveLayer.Opacity = (byte)Math.Round(e.NewValue);
        Canvas.RenderComposite();
        Canvas.InvalidateVisual();
    }

    private void OnOpacityCommitted(object? sender, Avalonia.Input.PointerReleasedEventArgs e) => CommitOpacityChange();

    private void CommitOpacityChange()
    {
        var layer = _opacityLayer;
        if (layer is null || _opacityBefore is null) return;
        byte before = _opacityBefore.Value, after = layer.Opacity;
        _opacityBefore = null;
        _opacityLayer = null;
        if (before == after) return;

        // Only the settled value is recorded, not every frame of the slider drag: the intermediate
        // values leave no trace in the document, and a replay of them would just be noise.
        RecordAction("layer.opacity." + after);
        Canvas.History.Push(new DelegateMemento("Opacity",
            () => layer.Opacity = before, () => layer.Opacity = after));
    }
}
