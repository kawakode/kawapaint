// KawaPaint - asks where batch-applied script output should go. Defaults to a separate output
// folder rather than overwriting originals: effects/transforms run unattended over many files are
// lossy, and a forgotten default that silently replaces source files is the one mistake here a
// user can't easily walk back from.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace KawaPaint.App;

public sealed class BatchApplyDialog : Window
{
    private readonly RadioButton _toFolder;
    private readonly RadioButton _inPlace;
    private readonly TextBox _folderPath;
    private readonly CheckBox _stopOnError;
    private readonly IStorageProvider _storage;
    private IStorageFolder? _outputFolder;

    public IStorageFolder? OutputFolder => _toFolder.IsChecked == true ? _outputFolder : null;
    public bool InPlace => _inPlace.IsChecked == true;
    public bool StopOnError => _stopOnError.IsChecked == true;

    public BatchApplyDialog(IStorageProvider storage)
    {
        _storage = storage;

        Title = "Batch Apply Script";
        Width = 440;
        SizeToContent = SizeToContent.Height;   // the folder hint below grows the window when it shows
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // "Save to folder" with no folder chosen is the one unrunnable combination. Saying so beats
        // the old behaviour, where Run simply did nothing and left the user clicking a button that
        // looked enabled and gave no reason for ignoring them. Declared up here because both the
        // Browse handler and Run need to reach it.
        var hint = new TextBlock
        {
            Text = "Choose an output folder first.",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x80, 0x50)),
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap
        };

        _toFolder = new RadioButton { Content = "Save to folder:", GroupName = "output", IsChecked = true };
        _folderPath = new TextBox { PlaceholderText = "Choose a folder…", IsReadOnly = true, Margin = new Thickness(24, 0, 0, 0) };
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var folders = await _storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Choose an output folder", AllowMultiple = false });
            if (folders.Count > 0)
            {
                _outputFolder = folders[0];
                _folderPath.Text = folders[0].Name;
                _toFolder.IsChecked = true;
                hint.IsVisible = false;
            }
        };

        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24, 4, 0, 0) };
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(_folderPath);
        folderRow.Children.Add(browse);

        _inPlace = new RadioButton { Content = "Overwrite the original files", GroupName = "output" };
        _stopOnError = new CheckBox { Content = "Stop a file's script on the first problem", Margin = new Thickness(0, 8, 0, 0) };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Content = "Run", IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (_toFolder.IsChecked == true && _outputFolder is null) { hint.IsVisible = true; return; }
            Close(true);
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Thickness(0, 16, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        _inPlace.IsCheckedChanged += (_, _) => hint.IsVisible = false;

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 6,
            Children = { _toFolder, folderRow, _inPlace, _stopOnError, hint, buttons }
        };
    }
}
