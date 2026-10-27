// KawaPaint - menus for the touch interfaces: the phone's ☰ drawer, which lays the menu bar out
// as a list a finger can work through, and the quick menu a long press on the canvas opens with
// the current tool's settings, a few colours and undo.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KawaPaint.App.Core;
using KawaPaint.Engine;

namespace KawaPaint.App;

public partial class MainView
{
    private Panel? _touchOverlay;

    private static readonly IBrush TouchCardBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly IBrush TouchTextBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
    private static readonly IBrush TouchDimBrush = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));

    public bool IsTouchOverlayOpen => _touchOverlay is not null;

    /// <summary>Puts <paramref name="content"/> over everything; a tap outside it closes it.</summary>
    private void ShowTouchOverlay(Control content, bool shade)
    {
        CloseTouchOverlay();
        var backdrop = new Border
        {
            Background = shade ? new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)) : Brushes.Transparent
        };
        backdrop.PointerPressed += (_, e) =>
        {
            CloseTouchOverlay();
            e.Handled = true;
        };
        _touchOverlay = new Panel { Name = "TouchOverlay" };
        _touchOverlay.Children.Add(backdrop);
        _touchOverlay.Children.Add(content);
        RootPanel.Children.Add(_touchOverlay);
    }

    private void CloseTouchOverlay()
    {
        if (_touchOverlay is null) return;
        RootPanel.Children.Remove(_touchOverlay);
        _touchOverlay = null;
    }

    // ---- ☰ drawer ------------------------------------------------------------

    /// <summary>
    /// The phone's main menu: the menu bar's own items, read afresh on every opening so recent
    /// files, layouts and check marks are current, as a scrolling list sliding in from the left.
    /// Each top-level menu is a section that expands in place.
    /// </summary>
    private void OpenMainDrawer()
    {
        var list = new StackPanel();
        AddDrawerItems(list, MainMenu.Items.OfType<object>(), depth: 0);

        var title = new TextBlock
        {
            Text = "KawaPaint",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = TouchTextBrush,
            Margin = new Thickness(18, 16, 18, 10)
        };
        DockPanel.SetDock(title, Dock.Top);
        var body = new DockPanel();
        body.Children.Add(title);
        body.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list
        });

        var drawer = new Border
        {
            Name = "MainDrawer",
            Width = Math.Min(320, Math.Max(240, Bounds.Width * 0.85)),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = TouchCardBrush,
            BoxShadow = BoxShadows.Parse("4 0 16 0 #A0000000"),
            Child = body
        };
        ShowTouchOverlay(drawer, shade: true);
    }

    private void AddDrawerItems(StackPanel host, IEnumerable<object> items, int depth)
    {
        bool pendingRule = false;
        foreach (var item in items)
        {
            if (item is Separator)
            {
                pendingRule = host.Children.Count > 0;
                continue;
            }
            if (item is not MenuItem { IsVisible: true } menuItem) continue;
            if (pendingRule)
            {
                host.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(16 + depth * 16, 4, 12, 4),
                    Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44))
                });
                pendingRule = false;
            }

            string label = MenuLabel(menuItem.Header);
            var children = menuItem.Items.OfType<object>().Where(c => c is MenuItem or Separator).ToList();
            if (children.OfType<MenuItem>().Any())
            {
                var section = new StackPanel { IsVisible = false };
                var header = DrawerRow("▸  " + label, depth, bold: depth == 0);
                header.Click += (_, _) =>
                {
                    section.IsVisible = !section.IsVisible;
                    ((TextBlock)header.Content!).Text = (section.IsVisible ? "▾  " : "▸  ") + label;
                    if (section.IsVisible && section.Children.Count == 0)
                        AddDrawerItems(section, menuItem.Items.OfType<object>(), depth + 1);
                };
                host.Children.Add(header);
                host.Children.Add(section);
            }
            else
            {
                bool check = menuItem.ToggleType != MenuItemToggleType.None && menuItem.IsChecked;
                var row = DrawerRow((check ? "✓  " : "    ") + label, depth, bold: false);
                row.IsEnabled = menuItem.IsEnabled;
                row.Click += (_, _) =>
                {
                    CloseTouchOverlay();
                    // After the drawer is gone: the item may open a dialog over this very spot.
                    Dispatcher.UIThread.Post(() => ActivateMenuItem(menuItem));
                };
                host.Children.Add(row);
            }
        }
    }

    private Button DrawerRow(string text, int depth, bool bold) => new()
    {
        Content = new TextBlock
        {
            Text = text,
            FontSize = bold ? 16 : 15,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = TouchTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        },
        MinHeight = 44,
        Padding = new Thickness(14 + depth * 16, 0, 12, 0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = Brushes.Transparent,
        CornerRadius = new CornerRadius(0)
    };

    /// <summary>
    /// A menu header as plain text: no access-key underscores, and without the shortcut hint
    /// some headers carry after a run of spaces - there is no keyboard to press it on.
    /// </summary>
    internal static string MenuLabel(object? header)
    {
        string text = header as string ?? header?.ToString() ?? "";
        int hint = text.IndexOf("   ", StringComparison.Ordinal);
        if (hint > 0) text = text[..hint];
        return text.Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_").Trim();
    }

    private static void ActivateMenuItem(MenuItem item)
    {
        if (item.Command is { } command)
        {
            if (command.CanExecute(item.CommandParameter)) command.Execute(item.CommandParameter);
            return;
        }
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    }

    // ---- long-press quick menu ------------------------------------------------

    /// <summary>
    /// The current tool's settings where the finger is: size, hardness, tolerance, antialiasing,
    /// selection mode - whichever the tool uses, the same set the options bar enables for it -
    /// then a row of palette colours and undo/redo.
    /// </summary>
    private void ShowQuickMenu(Point canvasPoint)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = Canvas.CurrentTool.Name,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = TouchTextBrush
        });

        if (SizeGroup.IsEnabled)
            content.Children.Add(QuickSlider("Size", Canvas.BrushWidth, MinBrushSize, 200, ApplyBrushSize));
        if (BrushGroup.IsEnabled)
            content.Children.Add(QuickSlider("Hardness", (int)Math.Round(Canvas.BrushHardness * 100), 0, 100, ApplyBrushHardness, "%"));
        if (BucketGroup.IsEnabled)
            content.Children.Add(QuickSlider("Tolerance", Canvas.FillTolerance, MinTolerance, MaxTolerance, ApplyTolerance));

        var toggles = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        if (ShapeGroup.IsEnabled) toggles.Children.Add(QuickToggle("Antialias", AntialiasCheck));
        if (ShapeGroup.IsEnabled && FillShapesCheck.IsEnabled) toggles.Children.Add(QuickToggle("Fill", FillShapesCheck));
        if (BucketGroup.IsEnabled) toggles.Children.Add(QuickToggle("Global", GlobalFillCheck));
        if (toggles.Children.Count > 0) content.Children.Add(toggles);

        if (SelectGroup.IsEnabled)
        {
            var modes = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            foreach (var (mode, text) in new[]
                     {
                         (SelectionCombineMode.Replace, "New"), (SelectionCombineMode.Add, "Add"),
                         (SelectionCombineMode.Subtract, "Subtract"), (SelectionCombineMode.Intersect, "Intersect")
                     })
            {
                var button = new ToggleButton
                {
                    Content = text,
                    MinHeight = 40,
                    Padding = new Thickness(12, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsChecked = Canvas.SelectionCombineMode == mode
                };
                button.Click += (_, _) =>
                {
                    ApplySelectionCombineMode(mode);
                    foreach (var other in modes.Children.OfType<ToggleButton>()) other.IsChecked = ReferenceEquals(other, button);
                };
                modes.Children.Add(button);
            }
            content.Children.Add(modes);

            var actions = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            actions.Children.Add(QuickAction("Select All", () => OnSelectAll(this, new RoutedEventArgs())));
            actions.Children.Add(QuickAction("Deselect", () => OnSelectNone(this, new RoutedEventArgs())));
            actions.Children.Add(QuickAction("Invert", () => OnInvertSelection(this, new RoutedEventArgs())));
            actions.Children.Add(QuickAction("Crop", () => OnCropToSelection(this, new RoutedEventArgs())));
            content.Children.Add(actions);
        }

        content.Children.Add(QuickColors());

        var history = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        history.Children.Add(QuickAction("Undo", () => OnUndo(this, new RoutedEventArgs()), keepOpen: true));
        history.Children.Add(QuickAction("Redo", () => OnRedo(this, new RoutedEventArgs()), keepOpen: true));
        content.Children.Add(history);

        double width = Math.Min(320, Math.Max(200, Bounds.Width - 16));
        var card = new Border
        {
            Name = "QuickMenu",
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = TouchCardBrush,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            BoxShadow = BoxShadows.Parse("0 6 20 0 #A0000000"),
            Padding = new Thickness(14, 12),
            Child = content
        };

        // Above the finger, so the hand does not cover it, unless there is no room up there.
        card.Measure(new Size(width, double.PositiveInfinity));
        double height = card.DesiredSize.Height;
        Point at = Canvas.TranslatePoint(canvasPoint, RootPanel) ?? canvasPoint;
        double room = RootPanel.Bounds.Height;
        double x = Math.Clamp(at.X - width / 2, 8, Math.Max(8, RootPanel.Bounds.Width - width - 8));
        double y = at.Y - height - 32;
        if (y < 8) y = at.Y + 32;
        y = Math.Clamp(y, 8, Math.Max(8, room - height - 8));
        card.Margin = new Thickness(x, y, 0, 0);

        ShowTouchOverlay(card, shade: false);
    }

    private Control QuickSlider(string label, int value, int minimum, int maximum, Action<int> apply, string unit = "")
    {
        var readout = new TextBlock
        {
            Text = value + unit,
            Foreground = TouchTextBrush,
            MinWidth = 44,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var slider = new Slider
        {
            Name = "Quick" + label,
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            int v = (int)Math.Round(slider.Value);
            readout.Text = v + unit;
            apply(v);
        };

        var title = new TextBlock { Text = label, Foreground = TouchDimBrush, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*,Auto") };
        grid.Children.Add(title);
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        Grid.SetColumn(readout, 2);
        grid.Children.Add(readout);
        return grid;
    }

    /// <summary>Mirrors one of the options bar's check boxes, so the tool keeps a single source of truth.</summary>
    private static ToggleButton QuickToggle(string label, CheckBox source)
    {
        var toggle = new ToggleButton
        {
            Content = label,
            MinHeight = 40,
            Padding = new Thickness(14, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsChecked = source.IsChecked
        };
        toggle.IsCheckedChanged += (_, _) => source.IsChecked = toggle.IsChecked;
        return toggle;
    }

    private Button QuickAction(string label, Action action, bool keepOpen = false)
    {
        var button = new Button
        {
            Content = label,
            MinHeight = 40,
            Padding = new Thickness(14, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) =>
        {
            if (!keepOpen) CloseTouchOverlay();
            action();
        };
        return button;
    }

    private Control QuickColors()
    {
        var row = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        foreach (var entry in _palette.Colors.Take(14))
        {
            var color = entry.Color;
            var swatch = new Button
            {
                Width = 34,
                Height = 34,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(17),
                BorderThickness = new Thickness(2),
                BorderBrush = color == Canvas.BrushColor ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
                Background = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)),
                Classes = { "swatch" }
            };
            swatch.Click += (_, _) =>
            {
                SetForeground(color);
                CloseTouchOverlay();
            };
            row.Children.Add(swatch);
        }

        var more = new Button
        {
            Content = "More…",
            MinHeight = 34,
            Padding = new Thickness(12, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        more.Click += (_, _) =>
        {
            CloseTouchOverlay();
            if (!_panels.IsVisible("ColorWheel")) _panels.ToggleVisible("ColorWheel");
        };
        row.Children.Add(more);
        return row;
    }
}
