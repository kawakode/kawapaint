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

    // ---- back button ------------------------------------------------------------

    private void HookBackButton()
    {
        TopLevel? top = null;
        AttachedToVisualTree += (_, _) =>
        {
            top = TopLevel.GetTopLevel(this);
            if (top is not null) top.BackRequested += OnBackRequested;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (top is not null) top.BackRequested -= OnBackRequested;
            top = null;
        };
    }

    /// <summary>
    /// Android's Back closes whatever is on top - the ring, the drawer, a bottom sheet - before
    /// it is allowed to leave the app, and leaving with unsaved changes asks first. Unhandled, it
    /// finished the activity straight from an open drawer and took the drawing with it.
    /// </summary>
    private async void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (IsTouchOverlayOpen)
        {
            CloseTouchOverlay();
            e.Handled = true;
            return;
        }
        if (_canvasDialogOverlay is not null)
        {
            e.Handled = true;   // the dialog's own Cancel closes it
            return;
        }
        if (_panels.Panels.FirstOrDefault(p => _panels.IsSheet(p.Id)) is { } sheet)
        {
            _panels.SetPlace(sheet.Id, PanelPlace.Hidden);
            e.Handled = true;
            return;
        }
        if (!IsDirty || App.QuitApp is not { } quit) return;

        e.Handled = true;
        if (await ConfirmAsync("Quit KawaPaint", "The image has unsaved changes. Quit without saving them?", "Quit"))
            quit();
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

    // ---- long-press radial menu -----------------------------------------------

    private const double RadialButtonSize = 52;
    private const double RadialSpacing = 60;   // centre to centre along the ring
    private const double RadialMinRadius = 80;
    private const double RadialMaxArcRadius = 160;   // further than this is out of the thumb's reach

    private Point _radialPress;    // where the finger was
    private Point _radialCenter;   // the ring's centre: the press point, or moved on screen
    private Canvas? _radialCanvas;
    private Border? _radialValuePanel;

    /// <summary>
    /// The current tool's settings in a ring of round buttons around the finger: size, hardness,
    /// tolerance, antialiasing, selection mode - whichever the tool uses, the same set the
    /// options bar enables for it - then colour and undo/redo. A value opens a slider beside the
    /// ring; colour swaps the ring for the palette. Near an edge the ring becomes an arc that
    /// opens towards the middle of the screen.
    /// </summary>
    private void ShowQuickMenu(Point canvasPoint)
    {
        _radialPress = Canvas.TranslatePoint(canvasPoint, RootPanel) ?? canvasPoint;
        _radialCenter = _radialPress;
        _radialCanvas = new Canvas { Name = "QuickMenu" };
        _radialValuePanel = null;
        ShowTouchOverlay(_radialCanvas, shade: false);
        ShowRadialItems(ToolRadialItems());
    }

    private List<Control> ToolRadialItems()
    {
        var items = new List<Control>();

        if (SizeGroup.IsEnabled)
            items.Add(RadialValue("Size", () => Canvas.BrushWidth, MinBrushSize, 200, ApplyBrushSize, ""));
        if (BrushGroup.IsEnabled)
            items.Add(RadialValue("Hardness", () => (int)Math.Round(Canvas.BrushHardness * 100), 0, 100, ApplyBrushHardness, "%"));
        if (BucketGroup.IsEnabled)
            items.Add(RadialValue("Tolerance", () => Canvas.FillTolerance, MinTolerance, MaxTolerance, ApplyTolerance, ""));
        if (ShapeGroup.IsEnabled) items.Add(RadialToggle("Antialias", "AA", AntialiasCheck));
        if (ShapeGroup.IsEnabled && FillShapesCheck.IsEnabled) items.Add(RadialToggle("Fill", "Fill", FillShapesCheck));
        if (BucketGroup.IsEnabled) items.Add(RadialToggle("Global", "All", GlobalFillCheck));

        if (SelectGroup.IsEnabled)
        {
            // One button that steps through the four combine modes, labelled with the current one.
            var modes = new[]
            {
                (SelectionCombineMode.Replace, "New"), (SelectionCombineMode.Add, "+"),
                (SelectionCombineMode.Subtract, "−"), (SelectionCombineMode.Intersect, "∩")
            };
            string Label() => modes.First(m => m.Item1 == Canvas.SelectionCombineMode).Item2;
            var text = RadialText(Label());
            var mode = RadialButton("Mode", text, "Selection mode", keepOpen: true);
            mode.Click += (_, _) =>
            {
                int i = Array.FindIndex(modes, m => m.Item1 == Canvas.SelectionCombineMode);
                ApplySelectionCombineMode(modes[(i + 1) % modes.Length].Item1);
                text.Text = Label();
            };
            items.Add(mode);
            items.Add(RadialCommand("SelectAll", "Select All", () => OnSelectAll(this, new RoutedEventArgs())));
            items.Add(RadialCommand("Deselect", "Deselect", () => OnSelectNone(this, new RoutedEventArgs())));
            items.Add(RadialCommand("InvertSel", "Invert Selection", () => OnInvertSelection(this, new RoutedEventArgs())));
            items.Add(RadialCommand("Crop", "Crop to Selection", () => OnCropToSelection(this, new RoutedEventArgs())));
        }

        var swatch = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Background = ColorBrush(Canvas.BrushColor)
        };
        var colour = RadialButton("Color", swatch, "Colour", keepOpen: true);
        colour.Click += (_, _) => ShowRadialItems(PaletteRadialItems());
        items.Add(colour);

        var undo = RadialButton("Undo", Icons.Create("Undo", 20), "Undo", keepOpen: true);
        undo.Click += (_, _) => OnUndo(this, new RoutedEventArgs());
        items.Add(undo);
        var redo = RadialButton("Redo", Icons.Create("Redo", 20), "Redo", keepOpen: true);
        redo.Click += (_, _) => OnRedo(this, new RoutedEventArgs());
        items.Add(redo);
        return items;
    }

    private List<Control> PaletteRadialItems()
    {
        var items = new List<Control>();
        foreach (var entry in _palette.Colors.Take(12))
        {
            var color = entry.Color;
            var swatch = new Button
            {
                Name = "RadialSwatch",
                Width = 44,
                Height = 44,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(22),
                BorderThickness = new Thickness(2),
                BorderBrush = color == Canvas.BrushColor ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
                Background = ColorBrush(color),
                Classes = { "swatch" }
            };
            swatch.Click += (_, _) =>
            {
                SetForeground(color);
                CloseTouchOverlay();
            };
            items.Add(swatch);
        }
        var more = RadialButton("MoreColors", RadialText("…"), "More colours");
        more.Click += (_, _) =>
        {
            if (!_panels.IsVisible("ColorWheel")) _panels.ToggleVisible("ColorWheel");
        };
        items.Add(more);
        var back = RadialButton("Back", RadialText("‹"), "Back", keepOpen: true);
        back.Click += (_, _) => ShowRadialItems(ToolRadialItems());
        items.Add(back);
        return items;
    }

    /// <summary>Lays <paramref name="items"/> out around the press point, replacing the current ring.</summary>
    private void ShowRadialItems(List<Control> items)
    {
        if (_radialCanvas is null) return;
        _radialCanvas.Children.Clear();
        _radialValuePanel = null;

        // The hub, where the finger was: a close button, so it cannot be mistaken for the
        // colour button on the ring.
        var hub = new Button
        {
            Name = "RadialHub",
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(18),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1C)),
            Content = Icons.Create("Close", 16)
        };
        ToolTip.SetTip(hub, "Close");
        hub.Click += (_, _) => CloseTouchOverlay();
        // Keep the ring over the canvas, off the toolbars and panels, when the canvas has room.
        var area = new Rect(RootPanel.Bounds.Size);
        var canvasArea = Canvas.TranslatePoint(default, RootPanel) is { } origin ? new Rect(origin, Canvas.Bounds.Size).Intersect(area) : area;
        double diameter = 2 * (RingRadius(items.Count) + 8 + RadialButtonSize / 2);
        if (canvasArea.Width >= diameter && canvasArea.Height >= diameter && canvasArea.Contains(_radialPress)) area = canvasArea;
        var positions = RingPositions(items.Count, _radialPress, area, out _radialCenter);
        Place(hub, _radialCenter, 36);
        foreach (var (item, centre) in items.Zip(positions))
            Place(item, centre, item.Width);

        void Place(Control control, Point centre, double size)
        {
            Avalonia.Controls.Canvas.SetLeft(control, centre.X - size / 2);
            Avalonia.Controls.Canvas.SetTop(control, centre.Y - size / 2);
            _radialCanvas.Children.Add(control);
        }
    }

    /// <summary>
    /// Button centres for a ring of <paramref name="count"/> around <paramref name="centre"/>: a
    /// full circle starting at the top when it fits in <paramref name="area"/>, otherwise a half
    /// circle (or as much as fits) facing the middle of the area, as the finger is near an edge.
    /// An arc that would have to swing wider than a thumb reaches (a long ring such as the
    /// palette) is not used: the full circle moves inside the area instead, and
    /// <paramref name="hub"/> says where its centre went.
    /// </summary>
    public static List<Point> RingPositions(int count, Point centre, double width, double height) =>
        RingPositions(count, centre, new Rect(0, 0, width, height), out _);

    public static List<Point> RingPositions(int count, Point centre, double width, double height, out Point hub) =>
        RingPositions(count, centre, new Rect(0, 0, width, height), out hub);

    private static double RingRadius(int count) => Math.Max(RadialMinRadius, count * RadialSpacing / (2 * Math.PI));

    public static List<Point> RingPositions(int count, Point centre, Rect area, out Point hub)
    {
        const double inset = 8 + RadialButtonSize / 2;
        hub = centre;
        double radius = RingRadius(count);
        var circle = Arc(-Math.PI / 2, 2 * Math.PI, radius, closed: true);
        if (circle.All(Fits)) return circle;

        double facing = Math.Atan2(area.Center.Y - centre.Y, area.Center.X - centre.X);
        foreach (double span in new[] { Math.PI, Math.PI * 0.75, Math.PI * 0.5 })
        {
            double arcRadius = Math.Max(RadialMinRadius, (count - 1) * RadialSpacing / span);
            if (arcRadius > RadialMaxArcRadius) break;
            var arc = Arc(facing - span / 2, span, arcRadius, closed: false);
            if (arc.All(Fits)) return arc;
        }
        // Move the whole circle away from the edges, keeping the finger's side where it can.
        double reach = radius + inset;
        hub = centre = new Point(
            Math.Clamp(centre.X, area.Left + reach, Math.Max(area.Left + reach, area.Right - reach)),
            Math.Clamp(centre.Y, area.Top + reach, Math.Max(area.Top + reach, area.Bottom - reach)));
        circle = Arc(-Math.PI / 2, 2 * Math.PI, radius, closed: true);
        if (circle.All(Fits)) return circle;
        // A tiny screen: keep the circle and pull stragglers inside.
        return circle.Select(p => new Point(
            Math.Clamp(p.X, area.Left + inset, Math.Max(area.Left + inset, area.Right - inset)),
            Math.Clamp(p.Y, area.Top + inset, Math.Max(area.Top + inset, area.Bottom - inset)))).ToList();

        bool Fits(Point p) => p.X >= area.Left + inset && p.Y >= area.Top + inset && p.X <= area.Right - inset && p.Y <= area.Bottom - inset;

        List<Point> Arc(double start, double span, double r, bool closed)
        {
            var points = new List<Point>(count);
            double step = count <= 1 ? 0 : span / (closed ? count : count - 1);
            for (int i = 0; i < count; i++)
            {
                double angle = start + i * step;
                points.Add(new Point(centre.X + r * Math.Cos(angle), centre.Y + r * Math.Sin(angle)));
            }
            return points;
        }
    }

    private static TextBlock RadialText(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        Foreground = TouchTextBrush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center
    };

    private Button RadialButton(string name, object content, string tip, bool keepOpen = false)
    {
        var button = new Button
        {
            Name = "Radial" + name,
            Width = RadialButtonSize,
            Height = RadialButtonSize,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(RadialButtonSize / 2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = TouchCardBrush,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)),
            BorderThickness = new Thickness(1),
            Content = content
        };
        ToolTip.SetTip(button, tip);
        if (!keepOpen) button.Click += (_, _) => CloseTouchOverlay();
        return button;
    }

    private Button RadialCommand(string icon, string tip, Action action)
    {
        var button = RadialButton(icon, Icons.Create(icon, 20), tip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Mirrors one of the options bar's check boxes, so the tool keeps a single source of truth.</summary>
    private Button RadialToggle(string name, string label, CheckBox source)
    {
        var text = RadialText(label);
        var button = RadialButton(name, text, name, keepOpen: true);
        void Sync() => button.BorderBrush = source.IsChecked == true
            ? new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0xE6))
            : new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));
        button.BorderThickness = new Thickness(3);
        Sync();
        button.Click += (_, _) =>
        {
            source.IsChecked = !(source.IsChecked ?? false);
            Sync();
        };
        return button;
    }

    /// <summary>A button showing a value; tapping it opens a slider for it beside the ring.</summary>
    private Button RadialValue(string name, Func<int> read, int minimum, int maximum, Action<int> apply, string unit)
    {
        var text = new TextBlock
        {
            FontSize = 13,
            Foreground = TouchTextBrush,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        void Show() => text.Inlines = new Avalonia.Controls.Documents.InlineCollection
        {
            new Avalonia.Controls.Documents.Run(name[..Math.Min(4, name.Length)]) { FontSize = 10, Foreground = TouchDimBrush },
            new Avalonia.Controls.Documents.LineBreak(),
            new Avalonia.Controls.Documents.Run(read() + unit) { FontWeight = FontWeight.SemiBold }
        };
        Show();
        var button = RadialButton(name, text, name, keepOpen: true);
        button.Click += (_, _) => ShowRadialSlider(name, read(), minimum, maximum, v =>
        {
            apply(v);
            Show();
        }, unit);
        return button;
    }

    private void ShowRadialSlider(string name, int value, int minimum, int maximum, Action<int> apply, string unit)
    {
        if (_radialCanvas is null) return;
        if (_radialValuePanel is not null) _radialCanvas.Children.Remove(_radialValuePanel);

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
            Name = "Quick" + name,
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
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(new TextBlock { Text = name, Foreground = TouchDimBrush, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        Grid.SetColumn(readout, 2);
        grid.Children.Add(readout);

        double width = Math.Min(300, RootPanel.Bounds.Width - 16);
        _radialValuePanel = new Border
        {
            Width = width,
            Background = TouchCardBrush,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            BoxShadow = BoxShadows.Parse("0 4 14 0 #A0000000"),
            Padding = new Thickness(16, 4),
            Child = grid
        };
        // Beyond the ring, on whichever side of the finger has more room.
        double reach = RadialMinRadius + RadialButtonSize;
        foreach (var child in _radialCanvas.Children)
            reach = Math.Max(reach, Math.Abs(Avalonia.Controls.Canvas.GetTop(child) + child.Height / 2 - _radialCenter.Y) + RadialButtonSize / 2 + 12);
        double panelHeight = 48;
        double height = RootPanel.Bounds.Height;
        double y = _radialCenter.Y > height / 2 ? _radialCenter.Y - reach - panelHeight : _radialCenter.Y + reach;
        y = Math.Clamp(y, 8, Math.Max(8, height - panelHeight - 8));
        double x = Math.Clamp(_radialCenter.X - width / 2, 8, Math.Max(8, RootPanel.Bounds.Width - width - 8));
        Avalonia.Controls.Canvas.SetLeft(_radialValuePanel, x);
        Avalonia.Controls.Canvas.SetTop(_radialValuePanel, y);
        _radialCanvas.Children.Add(_radialValuePanel);
    }

    private static SolidColorBrush ColorBrush(ColorBgra c) => new(Color.FromArgb(c.A, c.R, c.G, c.B));
}
