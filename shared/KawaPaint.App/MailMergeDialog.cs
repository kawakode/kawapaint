using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using KawaPaint.Engine.Codecs;
using KawaPaint.Engine.Exporting;
using KawaPaint.Engine.MailMerge;

namespace KawaPaint.App;

public sealed class MailMergeDialog : Window
{
    private readonly IStorageProvider _storage;
    private readonly TextBox _csv = new() { IsReadOnly = true };
    private readonly TextBox _folder = new() { IsReadOnly = true };
    private readonly ComboBox _preset = new();
    private readonly TextBox _pattern = new() { Text = "{name}-{row}.{ext}" };
    private readonly TextBlock _headers = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public string? CsvPath { get; private set; }
    public string? OutputFolder { get; private set; }
    public string PresetName => (_preset.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    public string FilenamePattern => _pattern.Text?.Trim() ?? "{name}-{row}.{ext}";

    public MailMergeDialog(IStorageProvider storage, IReadOnlyDictionary<string, ExportPreset> presets, int zoneCount)
    {
        _storage = storage;
        Title = "Mail Merge from CSV"; Width = 570; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var (name, preset) in presets.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var codec = CodecRegistry.FindById(preset.CodecId);
            if (codec is not { CanEncode: true, IsAvailable: true }) continue;
            _preset.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        }
        _preset.SelectedIndex = _preset.ItemCount > 0 ? 0 : -1;

        var csvBrowse = new Button { Content = "Browse…" };
        csvBrowse.Click += async (_, _) =>
        {
            var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose CSV data", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("CSV data") { Patterns = new[] { "*.csv" } } }
            });
            if (files.FirstOrDefault() is not { } file || LocalPath(file) is not { } path) return;
            try
            {
                var data = CsvData.Load(path);
                CsvPath = path; _csv.Text = path;
                _headers.Text = "CSV fields: " + string.Join(", ", data.Headers.Select(h => "{" + h + "}")) +
                    $" · {data.Rows.Count} output row(s)";
                if (data.Headers.Count > 0) _pattern.Text = "{name}-{" + data.Headers[0] + "}.{ext}";
            }
            catch (Exception ex) { _headers.Text = "Could not read CSV: " + ex.Message; CsvPath = null; }
        };
        var folderBrowse = new Button { Content = "Browse…" };
        folderBrowse.Click += async (_, _) =>
        {
            var folders = await _storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Choose output folder", AllowMultiple = false });
            if (folders.FirstOrDefault() is { } folder && LocalPath(folder) is { } path)
            { OutputFolder = path; _folder.Text = path; }
        };

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("135,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), RowSpacing = 8, ColumnSpacing = 8 };
        AddRow(form, 0, "CSV data", _csv, csvBrowse);
        AddRow(form, 1, "Output folder", _folder, folderBrowse);
        AddRow(form, 2, "Export preset", _preset, null);
        AddRow(form, 3, "Filename pattern", _pattern, null);

        var cancel = new Button { Content = "Cancel", IsCancel = true }; cancel.Click += (_, _) => Close(false);
        var run = new Button { Content = "Create Images", IsDefault = true };
        run.Click += (_, _) => { if (CsvPath is not null && OutputFolder is not null && PresetName.Length > 0) Close(true); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Children = { cancel, run } };
        Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children =
        {
            new TextBlock { Text = $"This template contains {zoneCount} dynamic text zone(s). One image will be created per CSV row." },
            form, _headers, new TextBlock { Text = "Filename tokens may include {row}, {name}, {ext}, and any {CSV Header}.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons
        } };
    }

    private static void AddRow(Grid grid, int row, string label, Control value, Button? button)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row); Grid.SetColumn(value, 1); Grid.SetRow(value, row);
        grid.Children.Add(text); grid.Children.Add(value);
        if (button is not null) { Grid.SetColumn(button, 2); Grid.SetRow(button, row); grid.Children.Add(button); }
    }

    private static string? LocalPath(IStorageItem item)
    {
        try { return item.Path.IsAbsoluteUri && item.Path.IsFile ? item.Path.LocalPath : null; }
        catch { return null; }
    }
}
