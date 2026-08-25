// KawaPaint - shows what metadata the chosen files carry, and asks where the cleaned copies go.
//
// It reports before it acts, which is the whole reason this is a dialog and not a menu item that
// just does it: "remove metadata" is irreversible, and the interesting question ("does this photo
// know where I live?") is answered by the listing, not by the removal. The findings are re-rendered
// when the ICC checkbox changes so the keep/remove column always matches what the button will do.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using KawaPaint.Engine.Metadata;

namespace KawaPaint.App;

public sealed class MetadataDialog : Window
{
    private readonly RadioButton _toFolder;
    private readonly RadioButton _inPlace;
    private readonly CheckBox _keepIcc;
    private readonly RadioButton _removeAll;
    private readonly RadioButton _targeted;
    private readonly CheckBox _removeGps;
    private readonly TextBox _make = new() { PlaceholderText = "leave unchanged" };
    private readonly TextBox _model = new() { PlaceholderText = "leave unchanged" };
    private readonly TextBox _captured = new() { PlaceholderText = "YYYY:MM:DD HH:MM:SS or leave unchanged" };
    private readonly TextBox _findings;
    private readonly TextBlock _summary;
    private readonly IStorageProvider _storage;
    private readonly IReadOnlyList<(string Name, MetadataReport Report)> _scans;
    private IStorageFolder? _outputFolder;

    public MetadataStripOptions Options => new() { KeepColorProfile = _keepIcc.IsChecked == true };
    public MetadataEditOptions? EditOptions => _targeted.IsChecked == true ? new MetadataEditOptions
    {
        RemoveGps = _removeGps.IsChecked == true,
        CameraMake = string.IsNullOrWhiteSpace(_make.Text) ? null : _make.Text,
        CameraModel = string.IsNullOrWhiteSpace(_model.Text) ? null : _model.Text,
        Captured = string.IsNullOrWhiteSpace(_captured.Text) ? null : _captured.Text
    } : null;
    public IStorageFolder? OutputFolder => _toFolder.IsChecked == true ? _outputFolder : null;
    public bool InPlace => _inPlace.IsChecked == true;

    public MetadataDialog(IStorageProvider storage, IReadOnlyList<(string Name, MetadataReport Report)> scans)
    {
        _storage = storage;
        _scans = scans;

        Title = "Image Metadata";
        Width = 620;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

        _findings = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = "Consolas,Menlo,monospace"
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_findings, ScrollBarVisibility.Auto);

        // Stated plainly because it is the non-obvious property of this feature and the reason to
        // use it rather than re-exporting: the compressed pixel data is copied through untouched,
        // so a JPEG cleaned this way loses no quality at all.
        var lossless = new TextBlock
        {
            Text = "Pixels are copied through untouched - nothing is re-encoded, so image quality is unchanged.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 8, 0, 0)
        };

        _keepIcc = new CheckBox { Content = "Keep ICC colour profiles", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        _keepIcc.IsCheckedChanged += (_, _) => Render();

        _removeAll = new RadioButton { Content = "Remove all metadata", GroupName = "metaAction", IsChecked = true };
        _targeted = new RadioButton { Content = "Edit EXIF / remove GPS only", GroupName = "metaAction" };
        _removeGps = new CheckBox { Content = "Remove GPS location", IsChecked = true };
        var editGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("110,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            RowSpacing = 4,
            Margin = new Thickness(24, 2, 0, 6)
        };
        Control[] editControls = { _removeGps, _make, _model, _captured };
        string[] editLabels = { "", "Camera make", "Camera model", "Captured" };
        for (int row = 0; row < editControls.Length; row++)
        {
            var label = new TextBlock { Text = editLabels[row], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row); Grid.SetRow(editControls[row], row); Grid.SetColumn(editControls[row], 1);
            editGrid.Children.Add(label); editGrid.Children.Add(editControls[row]);
        }
        void UpdateActionState()
        {
            bool targeted = _targeted.IsChecked == true;
            editGrid.IsEnabled = targeted;
            _keepIcc.IsEnabled = !targeted;
            Render();
        }
        _removeAll.IsCheckedChanged += (_, _) => UpdateActionState();
        _targeted.IsCheckedChanged += (_, _) => UpdateActionState();
        editGrid.IsEnabled = false;

        var hint = new TextBlock
        {
            Text = "Choose an output folder first.",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x80, 0x50)),
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap
        };

        // Same default as BatchApplyDialog, and for the same reason: the destructive option should
        // never be the one a user lands on by not reading. Metadata removal cannot be undone from
        // inside the app - the original bytes are simply gone.
        _toFolder = new RadioButton { Content = "Save cleaned copies to folder:", GroupName = "metaOutput", IsChecked = true };
        var folderPath = new TextBox { PlaceholderText = "Choose a folder…", IsReadOnly = true };
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var folders = await _storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Choose an output folder", AllowMultiple = false });
            if (folders.Count > 0)
            {
                _outputFolder = folders[0];
                folderPath.Text = folders[0].Name;
                _toFolder.IsChecked = true;
                hint.IsVisible = false;
            }
        };

        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24, 4, 0, 0) };
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(folderPath);
        folderRow.Children.Add(browse);

        _inPlace = new RadioButton { Content = "Overwrite the original files", GroupName = "metaOutput" };
        _inPlace.IsCheckedChanged += (_, _) => hint.IsVisible = false;

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);

        var remove = new Button { Content = "Remove Metadata", IsDefault = true };
        remove.Click += (_, _) =>
        {
            if (_toFolder.IsChecked == true && _outputFolder is null) { hint.IsVisible = true; return; }
            Close(true);
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(remove);

        var controls = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { lossless, _removeAll, _keepIcc, _targeted, editGrid,
                _toFolder, folderRow, _inPlace, hint, buttons }
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(16) };
        Grid.SetRow(_findings, 1);
        Grid.SetRow(controls, 2);
        root.Children.Add(_summary);
        root.Children.Add(_findings);
        root.Children.Add(controls);
        Content = root;

        Render();

        // Nothing removable under any setting: leave the listing readable but take the action away,
        // rather than offering a button whose only possible outcome is rewriting files identically.
        if (!scans.Any(s => s.Report.CanStrip && s.Report.HasAny))
        {
            remove.IsEnabled = false;
            remove.Content = "Nothing to Remove";
        }
    }

    private void Render()
    {
        var options = Options;

        int withMetadata = 0, located = 0, bytes = 0, unreadable = 0;
        var lines = new List<string>();

        foreach (var (name, report) in _scans)
        {
            var removable = _targeted.IsChecked == true
                ? report.Blocks.Where(block => block.Kind == MetadataKind.Exif).ToList()
                : report.Removable(options).ToList();
            if (removable.Count > 0) withMetadata++;
            if (report.HasLocation) located++;
            bytes += removable.Sum(b => b.Length);
            if (!report.CanStrip) unreadable++;

            lines.Add(report.Format.Length == 0 ? name : $"{name}  ({report.Format})");
            lines.Add(report.Describe(options));
            lines.Add("");
        }

        _findings.Text = string.Join(Environment.NewLine, lines);

        var summary = new List<string> { $"{_scans.Count} file(s)" };
        summary.Add(withMetadata == 0 ? "nothing to remove" : $"{withMetadata} carrying {MetadataReport.FormatSize(bytes)} of metadata");
        if (located > 0) summary.Add($"{located} with GPS location");
        if (unreadable > 0) summary.Add($"{unreadable} that cannot be rewritten safely");
        _summary.Text = string.Join(" · ", summary);
    }
}
