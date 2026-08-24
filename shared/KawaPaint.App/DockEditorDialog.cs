// KawaPaint - pick which commands and palette colors are pinned to the customizable dock.

using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KawaPaint.App.Core;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class DockEditorDialog : Window
{
    private readonly ListBox _available;
    private readonly ListBox _pinned;
    private readonly List<DockEntry> _pinnedEntries;

    /// <summary>Serialized form (DockEntry.Serialize), ready to write straight into WorkspaceSettings.DockCommands.</summary>
    public IReadOnlyList<string> ResultEntries => _pinnedEntries.Select(e => e.Serialize()).ToList();

    public DockEditorDialog(IReadOnlyList<AppCommand> availableCommands, IReadOnlyList<PaletteEntry> availableColors,
                             IReadOnlyList<string> initialPinned)
    {
        Title = "Customize Dock";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _pinnedEntries = initialPinned.Select(DockEntry.Parse).ToList();

        _available = new ListBox { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)) };
        foreach (var command in availableCommands)
            _available.Items.Add(new ListBoxItem { Content = $"{command.Category}: {command.Label}", Tag = DockEntry.ForCommand(command.Id) });
        foreach (var color in availableColors)
            _available.Items.Add(new ListBoxItem
            {
                Content = ColorRow(color.Color, string.IsNullOrEmpty(color.Name) ? color.Color.ToDisplayHexString() : color.Name),
                Tag = DockEntry.ForColor(color.Color.ToHexString())
            });

        _pinned = new ListBox { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)) };
        RefreshPinned(availableCommands, availableColors);

        var add = new Button { Content = "Add →", HorizontalAlignment = HorizontalAlignment.Stretch };
        add.Click += (_, _) =>
        {
            if (_available.SelectedItem is not ListBoxItem { Tag: DockEntry entry }) return;
            _pinnedEntries.Add(entry);
            RefreshPinned(availableCommands, availableColors);
        };

        var remove = new Button { Content = "← Remove", HorizontalAlignment = HorizontalAlignment.Stretch };
        remove.Click += (_, _) =>
        {
            if (_pinned.SelectedIndex < 0) return;
            _pinnedEntries.RemoveAt(_pinned.SelectedIndex);
            RefreshPinned(availableCommands, availableColors);
        };

        var up = new Button { Content = "↑", Width = 32 };
        up.Click += (_, _) => Move(-1, availableCommands, availableColors);
        var down = new Button { Content = "↓", Width = 32 };
        down.Click += (_, _) => Move(1, availableCommands, availableColors);

        var middleButtons = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Width = 90 };
        middleButtons.Children.Add(add);
        middleButtons.Children.Add(remove);

        var reorderButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        reorderButtons.Children.Add(up);
        reorderButtons.Children.Add(down);

        var pinnedColumn = new StackPanel { Spacing = 4 };
        pinnedColumn.Children.Add(new TextBlock { Text = "Pinned to dock" });
        pinnedColumn.Children.Add(_pinned);
        pinnedColumn.Children.Add(reorderButtons);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), ColumnSpacing = 8 };
        var availableColumn = new StackPanel { Spacing = 4 };
        availableColumn.Children.Add(new TextBlock { Text = "Available" });
        availableColumn.Children.Add(_available);
        Grid.SetColumn(availableColumn, 0);
        Grid.SetColumn(middleButtons, 1);
        Grid.SetColumn(pinnedColumn, 2);
        columns.Children.Add(availableColumn);
        columns.Children.Add(middleButtons);
        columns.Children.Add(pinnedColumn);

        var done = new Button { Content = "Done", IsDefault = true };
        done.Click += (_, _) => Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(done);

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16) };
        Grid.SetRow(columns, 0);
        Grid.SetRow(buttons, 1);
        root.Children.Add(columns);
        root.Children.Add(buttons);
        Content = root;

        _available.Height = 300;
        _pinned.Height = 260;
    }

    private static StackPanel ColorRow(ColorBgra color, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new Border
        {
            Width = 14,
            Height = 14,
            Background = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1)
        });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private void Move(int delta, IReadOnlyList<AppCommand> commands, IReadOnlyList<PaletteEntry> colors)
    {
        int i = _pinned.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _pinnedEntries.Count) return;

        (_pinnedEntries[i], _pinnedEntries[j]) = (_pinnedEntries[j], _pinnedEntries[i]);
        RefreshPinned(commands, colors);
        _pinned.SelectedIndex = j;
    }

    private void RefreshPinned(IReadOnlyList<AppCommand> commands, IReadOnlyList<PaletteEntry> colors)
    {
        _pinned.Items.Clear();
        foreach (var entry in _pinnedEntries)
        {
            object content;
            if (entry.Kind == DockEntryKind.Color)
            {
                // entry.Value is the stored AARRGGBB form; label the row with the readable one
                // rather than echoing it. A value that isn't a colour at all (hand-edited
                // settings) keeps its raw text next to an empty swatch, so it can be seen and removed.
                bool parsed = ColorBgra.TryParseHexString(entry.Value, out var color);
                content = ColorRow(parsed ? color : ColorBgra.Transparent,
                                   parsed ? color.ToDisplayHexString() : entry.Value);
            }
            else
            {
                content = commands.FirstOrDefault(c => c.Id == entry.Value)?.Label ?? entry.Value;
            }
            _pinned.Items.Add(new ListBoxItem { Content = content, Tag = entry });
        }
    }
}
