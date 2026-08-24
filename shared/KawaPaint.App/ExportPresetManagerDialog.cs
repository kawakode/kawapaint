// KawaPaint - create and edit the named recipes shown under File > Export.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using KawaPaint.Engine.Codecs;
using KawaPaint.Engine.Exporting;

namespace KawaPaint.App;

public sealed class ExportPresetManagerDialog : Window
{
    private readonly IStorageProvider _storage;
    private readonly Dictionary<string, ExportPreset> _presets;
    private readonly ListBox _list = new() { Width = 190 };
    private readonly ComboBox _codec = new();
    private readonly ComboBox _resizeMode = new();
    private readonly NumericUpDown _width = new() { Minimum = 1, Maximum = 100000, Increment = 1 };
    private readonly NumericUpDown _height = new() { Minimum = 1, Maximum = 100000, Increment = 1 };
    private readonly CheckBox _upscale = new() { Content = "Allow enlarging smaller images" };
    private readonly TextBox _padding = new();
    private readonly NumericUpDown _quality = new() { Minimum = 1, Maximum = 100, Increment = 1 };
    private readonly CheckBox _lossless = new() { Content = "Lossless (when supported)" };
    private readonly CheckBox _flatten = new() { Content = "Flatten before resizing" };
    private readonly TextBox _pattern = new();
    private readonly TextBox _folder = new() { IsReadOnly = true };
    private readonly TextBox _script = new() { IsReadOnly = true };
    private readonly TextBox _packageText = new() { AcceptsReturn = true, MinHeight = 70, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly CheckBox _copyText = new() { Content = "Copy package text after export" };
    private string? _currentName;
    private bool _loading;

    public IReadOnlyDictionary<string, ExportPreset> ResultPresets => _presets;

    public ExportPresetManagerDialog(IReadOnlyDictionary<string, ExportPreset> presets, IStorageProvider storage)
    {
        _storage = storage;
        _presets = presets.ToDictionary(kv => kv.Key, kv => Clone(kv.Value), StringComparer.OrdinalIgnoreCase);

        Title = "Manage Export Presets";
        Width = 760;
        Height = 650;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var codec in CodecRegistry.All.Where(c => c.CanEncode))
            _codec.Items.Add(new ComboBoxItem
            {
                Content = codec.DisplayName + (codec.IsAvailable ? "" : " (unavailable)"),
                Tag = codec.Id,
                IsEnabled = codec.IsAvailable
            });
        _resizeMode.ItemsSource = Enum.GetValues<ExportResizeMode>();

        _list.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            CommitCurrent();
            if (_list.SelectedItem is ListBoxItem { Tag: string name }) LoadPreset(name);
        };

        var add = new Button { Content = "Add…" };
        add.Click += async (_, _) =>
        {
            CommitCurrent();
            var prompt = new PromptDialog("New Export Preset", "New preset");
            if (await prompt.ShowDialog<bool>(this) != true) return;
            string name = prompt.ResultText.Trim();
            if (name.Length == 0 || _presets.ContainsKey(name)) return;
            _presets[name] = new ExportPreset();
            RefreshList(name);
        };
        var rename = new Button { Content = "Rename…" };
        rename.Click += async (_, _) =>
        {
            if (_currentName is null) return;
            CommitCurrent();
            var prompt = new PromptDialog("Rename Export Preset", _currentName);
            if (await prompt.ShowDialog<bool>(this) != true) return;
            string name = prompt.ResultText.Trim();
            if (name.Length == 0 || (!name.Equals(_currentName, StringComparison.OrdinalIgnoreCase) && _presets.ContainsKey(name))) return;
            var value = _presets[_currentName];
            _presets.Remove(_currentName);
            _presets[name] = value;
            RefreshList(name);
        };
        var remove = new Button { Content = "Delete" };
        remove.Click += (_, _) =>
        {
            if (_currentName is null) return;
            _presets.Remove(_currentName);
            _currentName = null;
            RefreshList(null);
        };

        var listButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { add, rename, remove }
        };
        var left = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 8 };
        Grid.SetRow(_list, 0); Grid.SetRow(listButtons, 1);
        left.Children.Add(_list); left.Children.Add(listButtons);

        var browseFolder = new Button { Content = "Browse…" };
        browseFolder.Click += async (_, _) =>
        {
            var folders = await _storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Preset output folder", AllowMultiple = false });
            if (folders.FirstOrDefault() is { } selected) _folder.Text = LocalPath(selected) ?? selected.Name;
        };
        var clearFolder = new Button { Content = "Clear" };
        clearFolder.Click += (_, _) => _folder.Text = "";

        var browseScript = new Button { Content = "Browse…" };
        browseScript.Click += async (_, _) =>
        {
            var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Optional script", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("KawaPaint script") { Patterns = new[] { "*.kpscript" } } }
            });
            if (files.FirstOrDefault() is { } selected) _script.Text = LocalPath(selected) ?? selected.Name;
        };
        var clearScript = new Button { Content = "Clear" };
        clearScript.Click += (_, _) => _script.Text = "";

        var editor = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 10,
            RowSpacing = 8
        };
        int row = 0;
        AddRow(editor, row++, "Format", _codec);
        AddRow(editor, row++, "Resize", _resizeMode);
        AddRow(editor, row++, "Width × height", Pair(_width, _height));
        AddRow(editor, row++, "", _upscale);
        AddRow(editor, row++, "Padding color", _padding);
        AddRow(editor, row++, "Quality", Pair(_quality, _lossless));
        AddRow(editor, row++, "", _flatten);
        AddRow(editor, row++, "Filename pattern", _pattern);
        AddRow(editor, row++, "Output folder", PathRow(_folder, browseFolder, clearFolder));
        AddRow(editor, row++, "Pre-export script", PathRow(_script, browseScript, clearScript));
        AddRow(editor, row++, "Caption / alt text", _packageText);
        AddRow(editor, row++, "", _copyText);

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Content = "Save", IsDefault = true };
        ok.Click += (_, _) =>
        {
            CommitCurrent();
            try { foreach (var preset in _presets.Values) PresetExporter.Validate(preset); }
            catch (Exception ex) { Title = "Manage Export Presets — " + ex.Message; return; }
            Close(true);
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Children = { cancel, ok }
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        Grid.SetColumn(left, 0); Grid.SetColumn(editor, 1);
        body.Children.Add(left); body.Children.Add(editor);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 12, Margin = new Thickness(16) };
        Grid.SetRow(body, 0); Grid.SetRow(buttons, 1);
        root.Children.Add(body); root.Children.Add(buttons);
        Content = root;

        RefreshList(_presets.Keys.FirstOrDefault());
    }

    private void RefreshList(string? select)
    {
        _loading = true;
        _list.Items.Clear();
        foreach (string name in _presets.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            _list.Items.Add(new ListBoxItem { Content = name, Tag = name });
        _loading = false;
        var item = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, select));
        if (item is not null) { _list.SelectedItem = item; LoadPreset((string)item.Tag!); }
        else SetEditorEnabled(false);
    }

    private void LoadPreset(string name)
    {
        _currentName = name;
        var p = _presets[name];
        SetEditorEnabled(true);
        _loading = true;
        _codec.SelectedItem = _codec.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, p.CodecId));
        _resizeMode.SelectedItem = p.ResizeMode;
        _width.Value = Math.Max(1, p.Width);
        _height.Value = Math.Max(1, p.Height);
        _upscale.IsChecked = p.AllowUpscale;
        _padding.Text = p.PaddingColor;
        _quality.Value = p.EncodeOptions?.Quality ?? 92;
        _lossless.IsChecked = p.EncodeOptions?.Lossless ?? false;
        _flatten.IsChecked = p.Flatten;
        _pattern.Text = p.FilenamePattern;
        _folder.Text = p.OutputFolder ?? "";
        _script.Text = p.ScriptPath ?? "";
        _packageText.Text = p.PackageText ?? "";
        _copyText.IsChecked = p.CopyPackageTextToClipboard;
        _loading = false;
    }

    private void CommitCurrent()
    {
        if (_loading || _currentName is null || !_presets.TryGetValue(_currentName, out var p)) return;
        p.CodecId = (_codec.SelectedItem as ComboBoxItem)?.Tag as string ?? p.CodecId;
        p.ResizeMode = _resizeMode.SelectedItem is ExportResizeMode mode ? mode : ExportResizeMode.None;
        p.Width = (int)(_width.Value ?? 1);
        p.Height = (int)(_height.Value ?? 1);
        p.AllowUpscale = _upscale.IsChecked == true;
        p.PaddingColor = _padding.Text?.Trim() ?? "FFFFFFFF";
        p.EncodeOptions = new EncodeOptions { Quality = (int)(_quality.Value ?? 92), Lossless = _lossless.IsChecked == true };
        p.Flatten = _flatten.IsChecked == true;
        p.FilenamePattern = _pattern.Text?.Trim() ?? "";
        p.OutputFolder = NullIfEmpty(_folder.Text);
        p.ScriptPath = NullIfEmpty(_script.Text);
        p.PackageText = NullIfEmpty(_packageText.Text);
        p.CopyPackageTextToClipboard = _copyText.IsChecked == true;
    }

    private void SetEditorEnabled(bool enabled)
    {
        foreach (var c in new Control[] { _codec, _resizeMode, _width, _height, _upscale, _padding,
                     _quality, _lossless, _flatten, _pattern, _folder, _script, _packageText, _copyText })
            c.IsEnabled = enabled;
    }

    private static void AddRow(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row); Grid.SetColumn(text, 0);
        Grid.SetRow(control, row); Grid.SetColumn(control, 1);
        grid.Children.Add(text); grid.Children.Add(control);
    }

    private static StackPanel Pair(params Control[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    private static Grid PathRow(TextBox text, params Button[] buttons)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        grid.Children.Add(text);
        for (int i = 0; i < buttons.Length; i++) { Grid.SetColumn(buttons[i], i + 1); grid.Children.Add(buttons[i]); }
        return grid;
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static string? LocalPath(IStorageItem item)
    {
        try { return item.Path.IsAbsoluteUri && item.Path.IsFile ? item.Path.LocalPath : null; }
        catch { return null; }
    }

    private static ExportPreset Clone(ExportPreset p)
    {
        var options = p.EncodeOptions ?? EncodeOptions.Default;
        return new()
    {
        CodecId = p.CodecId,
        EncodeOptions = new EncodeOptions { Quality = options.Quality, Lossless = options.Lossless,
            IconSizes = options.IconSizes.ToArray() },
        ResizeMode = p.ResizeMode, Width = p.Width, Height = p.Height, AllowUpscale = p.AllowUpscale,
        PaddingColor = p.PaddingColor, Flatten = p.Flatten, FilenamePattern = p.FilenamePattern,
        OutputFolder = p.OutputFolder, ScriptPath = p.ScriptPath, PackageText = p.PackageText,
        CopyPackageTextToClipboard = p.CopyPackageTextToClipboard
    };
    }
}
