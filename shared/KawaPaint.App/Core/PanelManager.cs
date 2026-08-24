// KawaPaint - the docking framework.
//
// Panels register a descriptor and this class owns everything else: docking to a side, floating,
// hiding, dragging, resizing and persistence. Adding a panel no longer means editing five
// methods and a settings class.
//
// Floating panels are overlay controls inside a Panel rather than real OS windows, because the
// browser head has no window or popup support at all - an in-canvas overlay is the only approach
// that works on every target.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace KawaPaint.App.Core;

public sealed class PanelManager
{
    private const double GripThickness = 6;

    /// <summary>How much of a floating panel must stay inside the window - enough of the title bar
    /// to grab, in both axes. See ClampToLayer.</summary>
    private const double MinVisible = 80;

    private readonly DockPanel _rootDock;
    private readonly Panel _floatingLayer;
    private readonly Control _fill;

    private readonly List<PanelDescriptor> _panels = new();
    private readonly Dictionary<string, FloatHost> _hosts = new(StringComparer.Ordinal);

    public PanelManager(DockPanel rootDock, Panel floatingLayer, Control fill, WorkspaceLayout layout)
    {
        _rootDock = rootDock;
        _floatingLayer = floatingLayer;
        _fill = fill;
        Layout = layout;

        // A layout saved on a larger window, or simply making this one smaller, would otherwise
        // leave floating panels sitting outside the visible area for good - see ClampToLayer.
        _floatingLayer.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty) ClampFloatingHosts();
        };
    }

    public WorkspaceLayout Layout { get; private set; }

    public IReadOnlyList<PanelDescriptor> Panels => _panels;

    /// <summary>Raised whenever placement or geometry changed and should be written to settings.</summary>
    public event EventHandler? LayoutChanged;

    public void Register(PanelDescriptor descriptor)
    {
        _panels.RemoveAll(p => p.Id == descriptor.Id);
        _panels.Add(descriptor);
        Layout.For(descriptor);
    }

    public PanelDescriptor? Find(string id) => _panels.FirstOrDefault(p => p.Id == id);

    public PanelPlacement PlacementOf(string id)
    {
        var descriptor = Find(id) ?? throw new ArgumentException($"Unknown panel '{id}'.", nameof(id));
        return Layout.For(descriptor);
    }

    /// <summary>Swaps in a different saved arrangement (a layout preset) and re-applies it.</summary>
    public void SetLayout(WorkspaceLayout layout)
    {
        Layout = layout;
        foreach (var descriptor in _panels) Layout.For(descriptor);
        Apply();
    }

    // ---- placement ---------------------------------------------------------

    public void SetPlace(string id, PanelPlace place)
    {
        var placement = PlacementOf(id);
        if (place != PanelPlace.Hidden) placement.LastShown = place;
        if (place is PanelPlace.Left or PanelPlace.Right or PanelPlace.Top or PanelPlace.Bottom)
            placement.LastDock = place;
        placement.Place = place;
        Apply();
        Notify();
    }

    /// <summary>Hides a visible panel, or restores a hidden one to wherever it was last shown.</summary>
    public void ToggleVisible(string id)
    {
        var placement = PlacementOf(id);
        SetPlace(id, placement.Place == PanelPlace.Hidden ? placement.LastShown : PanelPlace.Hidden);
    }

    /// <summary>Undocks a docked panel, or sends a floating one back to its last docked side.</summary>
    public void ToggleFloat(string id)
    {
        var placement = PlacementOf(id);
        SetPlace(id, placement.Place == PanelPlace.Floating ? placement.LastDock : PanelPlace.Floating);
    }

    public bool IsVisible(string id) => PlacementOf(id).Place != PanelPlace.Hidden;
    public bool IsFloating(string id) => PlacementOf(id).Place == PanelPlace.Floating;

    // ---- layout ------------------------------------------------------------

    public void Apply()
    {
        // A control can only have one parent, so detach everything before re-placing it.
        foreach (var descriptor in _panels)
        {
            _rootDock.Children.Remove(descriptor.Content);
            if (_hosts.TryGetValue(descriptor.Id, out var host)) host.Detach(descriptor.Content);
        }
        _rootDock.Children.Remove(_fill);
        foreach (var host in _hosts.Values) _floatingLayer.Children.Remove(host.Root);

        foreach (var descriptor in _panels)
        {
            var placement = Layout.For(descriptor);

            if (placement.Place == PanelPlace.Hidden)
            {
                descriptor.Content.IsVisible = false;
                continue;
            }
            descriptor.Content.IsVisible = true;

            bool floating = placement.Place == PanelPlace.Floating;
            foreach (var chrome in descriptor.DockedChrome) chrome.IsVisible = !floating;

            if (floating) PlaceFloating(descriptor, placement);
            else PlaceDocked(descriptor, placement);
        }

        _rootDock.Children.Add(_fill);   // fills whatever the docked panels left over
    }

    private void PlaceDocked(PanelDescriptor descriptor, PanelPlacement placement)
    {
        var dock = placement.Place switch
        {
            PanelPlace.Left => Dock.Left,
            PanelPlace.Right => Dock.Right,
            PanelPlace.Top => Dock.Top,
            _ => Dock.Bottom
        };

        DockPanel.SetDock(descriptor.Content, dock);

        // Sizing applies along the docking axis only: a left-docked panel gets a width and spans
        // the full height, and vice versa.
        double size = double.IsNaN(placement.DockSize) ? descriptor.DefaultDockSize : placement.DockSize;
        bool horizontal = dock is Dock.Left or Dock.Right;
        descriptor.Content.Width = horizontal ? size : double.NaN;
        descriptor.Content.Height = horizontal ? double.NaN : size;

        _rootDock.Children.Add(descriptor.Content);
    }

    private void PlaceFloating(PanelDescriptor descriptor, PanelPlacement placement)
    {
        // Floating panels size themselves, so drop any width the docked path had pinned on.
        descriptor.Content.Width = double.NaN;
        descriptor.Content.Height = double.NaN;

        var host = GetOrCreateHost(descriptor);
        host.Attach(descriptor.Content);
        host.Root.Width = placement.FloatWidth;
        host.Root.Height = placement.FloatHeight;
        host.Root.Margin = ClampToLayer(placement.FloatX, placement.FloatY);
        _floatingLayer.Children.Add(host.Root);
    }

    /// <summary>
    /// Keeps a floating panel's top-left inside the window. Without this a panel can be dragged
    /// out past the right or bottom edge - or restored there from a layout saved on a larger
    /// window - and its title bar, the only handle for moving or re-docking it, goes with it; the
    /// sole way back was View ▸ Panels ▸ Reset Layout. A strip of the panel is always kept
    /// on-screen rather than its whole width, so a wide panel near an edge still behaves.
    /// </summary>
    private Thickness ClampToLayer(double x, double y)
    {
        // Before the first layout pass the layer has no size yet and there is nothing to clamp to.
        if (_floatingLayer.Bounds.Width > 0) x = Math.Min(x, _floatingLayer.Bounds.Width - MinVisible);
        if (_floatingLayer.Bounds.Height > 0) y = Math.Min(y, _floatingLayer.Bounds.Height - MinVisible);

        return new Thickness(Math.Max(0, x), Math.Max(0, y), 0, 0);
    }

    /// <summary>Re-clamps every floating panel after the available area changed - shrinking the
    /// window would otherwise strand panels outside it. Deliberately does not Notify(): this is
    /// the window being resized, not the user rearranging anything, and it should not write
    /// settings on every frame of a resize drag.</summary>
    private void ClampFloatingHosts()
    {
        foreach (var host in _hosts.Values)
        {
            if (host.Root.Parent is null) continue;
            var clamped = ClampToLayer(host.Root.Margin.Left, host.Root.Margin.Top);
            if (clamped != host.Root.Margin) host.Root.Margin = clamped;
        }
    }

    private void Notify() => LayoutChanged?.Invoke(this, EventArgs.Empty);

    // ---- floating host -----------------------------------------------------

    private FloatHost GetOrCreateHost(PanelDescriptor descriptor)
    {
        if (_hosts.TryGetValue(descriptor.Id, out var existing)) return existing;

        var host = new FloatHost(this, descriptor);
        _hosts[descriptor.Id] = host;
        return host;
    }

    /// <summary>Highest ZIndex handed out so far. Monotonic - a floating panel is raised by taking
    /// the next value rather than by shuffling anything down.</summary>
    private int _topZ;

    private void BringToFront(Control root)
    {
        // ZIndex, not a reorder of _floatingLayer.Children. Removing and re-adding a control
        // detaches it from the visual tree, which drops the pointer capture a drag depends on -
        // so raising had to be skipped whenever the panel was already on top, and could not be
        // wired to the title bar at all (see the Tunnel handler in FloatHost). It also meant
        // Apply(), which rebuilds that collection in registration order, silently reshuffled the
        // stacking on every placement change. A ZIndex lives on the control, so it survives both.
        if (root.ZIndex == _topZ && _topZ != 0) return;
        root.ZIndex = ++_topZ;
    }

    /// <summary>Which edges a resize drag is moving. Corners combine two flags.</summary>
    [Flags]
    private enum Edge
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8
    }

    /// <summary>A floating panel: title bar for dragging, eight grips for resizing, content in the middle.</summary>
    private sealed class FloatHost
    {
        private readonly PanelManager _owner;
        private readonly PanelDescriptor _descriptor;
        private readonly DockPanel _contentDock;
        private readonly Border _titleBar;

        private Point _dragOrigin;
        private Rect _dragStartBounds;
        private Edge _resizing;
        private bool _moving;

        public FloatHost(PanelManager owner, PanelDescriptor descriptor)
        {
            _owner = owner;
            _descriptor = descriptor;

            _titleBar = BuildTitleBar();
            DockPanel.SetDock(_titleBar, Dock.Top);

            _contentDock = new DockPanel { LastChildFill = true };
            _contentDock.Children.Add(_titleBar);

            var frame = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
                BorderThickness = new Thickness(1),
                Child = _contentDock
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions($"{GripThickness},*,{GripThickness}"),
                RowDefinitions = new RowDefinitions($"{GripThickness},*,{GripThickness}")
            };
            Grid.SetRow(frame, 1);
            Grid.SetColumn(frame, 1);
            grid.Children.Add(frame);

            AddGrip(grid, 0, 0, Edge.Left | Edge.Top, StandardCursorType.TopLeftCorner);
            AddGrip(grid, 0, 1, Edge.Top, StandardCursorType.SizeNorthSouth);
            AddGrip(grid, 0, 2, Edge.Right | Edge.Top, StandardCursorType.TopRightCorner);
            AddGrip(grid, 1, 0, Edge.Left, StandardCursorType.SizeWestEast);
            AddGrip(grid, 1, 2, Edge.Right, StandardCursorType.SizeWestEast);
            AddGrip(grid, 2, 0, Edge.Left | Edge.Bottom, StandardCursorType.BottomLeftCorner);
            AddGrip(grid, 2, 1, Edge.Bottom, StandardCursorType.SizeNorthSouth);
            AddGrip(grid, 2, 2, Edge.Right | Edge.Bottom, StandardCursorType.BottomRightCorner);

            Root = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                BoxShadow = BoxShadows.Parse("0 4 16 0 #A0000000"),
                Child = grid
            };
            // Tunnel: the title bar's own press handler marks the event handled to start its drag,
            // so a bubbling handler here never saw the most natural way to raise a panel - clicking
            // its title bar - and only a click on the panel body raised it. Running first is safe
            // now that raising is only a ZIndex change and does not reparent anything.
            Root.AddHandler(InputElement.PointerPressedEvent, (_, _) => _owner.BringToFront(Root),
                            RoutingStrategies.Tunnel);
        }

        public Border Root { get; }

        public void Attach(Control content)
        {
            if (!_contentDock.Children.Contains(content)) _contentDock.Children.Add(content);
        }

        public void Detach(Control content) => _contentDock.Children.Remove(content);

        private Border BuildTitleBar()
        {
            var title = new TextBlock
            {
                Text = _descriptor.Title,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var dockBtn = new Button { Content = "▣", Padding = new Thickness(6, 0) };
            ToolTip.SetTip(dockBtn, "Dock this panel");
            dockBtn.Click += (_, _) => _owner.SetPlace(_descriptor.Id, _owner.PlacementOf(_descriptor.Id).LastDock);

            var closeBtn = new Button { Content = "✕", Padding = new Thickness(6, 0) };
            ToolTip.SetTip(closeBtn, "Hide (View ▸ Panels to restore)");
            closeBtn.Click += (_, _) => _owner.SetPlace(_descriptor.Id, PanelPlace.Hidden);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            buttons.Children.Add(dockBtn);
            buttons.Children.Add(closeBtn);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(title);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            var bar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Padding = new Thickness(8, 4),
                Cursor = new Cursor(StandardCursorType.SizeAll),
                Child = grid
            };

            bar.PointerPressed += OnMoveStart;
            bar.PointerMoved += OnMoveDelta;
            bar.PointerReleased += OnDragEnd;
            return bar;
        }

        private void AddGrip(Grid grid, int row, int column, Edge edge, StandardCursorType cursor)
        {
            // Transparent rather than null: a null background is not hit-testable in Avalonia.
            var grip = new Border { Background = Brushes.Transparent, Cursor = new Cursor(cursor) };
            Grid.SetRow(grip, row);
            Grid.SetColumn(grip, column);

            grip.PointerPressed += (_, e) => OnResizeStart(edge, e);
            grip.PointerMoved += OnResizeDelta;
            grip.PointerReleased += OnDragEnd;

            grid.Children.Add(grip);
        }

        // ---- drag to move ----

        private void OnMoveStart(object? sender, PointerPressedEventArgs e)
        {
            _moving = true;
            _dragOrigin = e.GetPosition(_owner._floatingLayer);
            _dragStartBounds = CurrentBounds();
            e.Pointer.Capture((IInputElement?)sender);
            e.Handled = true;
        }

        private void OnMoveDelta(object? sender, PointerEventArgs e)
        {
            if (!_moving) return;
            var delta = e.GetPosition(_owner._floatingLayer) - _dragOrigin;
            Root.Margin = _owner.ClampToLayer(_dragStartBounds.X + delta.X, _dragStartBounds.Y + delta.Y);
        }

        // ---- drag to resize ----

        private void OnResizeStart(Edge edge, PointerPressedEventArgs e)
        {
            _resizing = edge;
            _dragOrigin = e.GetPosition(_owner._floatingLayer);

            // Latch the current rendered size: until the first resize the host is sized to content
            // and Width/Height are NaN, which arithmetic would propagate.
            _dragStartBounds = CurrentBounds();
            e.Pointer.Capture(e.Source as IInputElement);
            e.Handled = true;
        }

        private void OnResizeDelta(object? sender, PointerEventArgs e)
        {
            if (_resizing == Edge.None) return;

            var delta = e.GetPosition(_owner._floatingLayer) - _dragOrigin;
            var (minWidth, minHeight) = MinimumSize();

            double x = _dragStartBounds.X, y = _dragStartBounds.Y;
            double width = _dragStartBounds.Width, height = _dragStartBounds.Height;

            if (_resizing.HasFlag(Edge.Right)) width = _dragStartBounds.Width + delta.X;
            if (_resizing.HasFlag(Edge.Bottom)) height = _dragStartBounds.Height + delta.Y;

            // Dragging a left or top edge moves the origin as well as the size, and the origin
            // must stop moving once the size hits its floor.
            if (_resizing.HasFlag(Edge.Left))
            {
                width = _dragStartBounds.Width - delta.X;
                if (width < minWidth) width = minWidth;
                x = _dragStartBounds.Right - width;
            }
            if (_resizing.HasFlag(Edge.Top))
            {
                height = _dragStartBounds.Height - delta.Y;
                if (height < minHeight) height = minHeight;
                y = _dragStartBounds.Bottom - height;
            }

            Root.Width = Math.Max(minWidth, width);
            Root.Height = Math.Max(minHeight, height);
            Root.Margin = _owner.ClampToLayer(x, y);
        }

        private void OnDragEnd(object? sender, PointerReleasedEventArgs e)
        {
            if (!_moving && _resizing == Edge.None) return;

            _moving = false;
            _resizing = Edge.None;
            e.Pointer.Capture(null);

            var placement = _owner.PlacementOf(_descriptor.Id);
            placement.FloatX = Root.Margin.Left;
            placement.FloatY = Root.Margin.Top;
            placement.FloatWidth = Root.Width;
            placement.FloatHeight = Root.Height;
            _owner.Notify();
        }

        private Rect CurrentBounds()
        {
            double width = double.IsNaN(Root.Width) ? Root.Bounds.Width : Root.Width;
            double height = double.IsNaN(Root.Height) ? Root.Bounds.Height : Root.Height;
            return new Rect(Root.Margin.Left, Root.Margin.Top, width, height);
        }

        /// <summary>
        /// The floor for interactive resizing: the descriptor's declared minimum, but never
        /// smaller than the title bar needs to stay operable, plus the grip border on each side.
        /// </summary>
        private (double Width, double Height) MinimumSize()
        {
            double chromeWidth = GripThickness * 2 + 2;
            double chromeHeight = GripThickness * 2 + 2 + Math.Max(_titleBar.Bounds.Height, 24);

            double width = Math.Max(_descriptor.MinWidth, _titleBar.DesiredSize.Width) + chromeWidth;
            double height = _descriptor.MinHeight + chromeHeight;
            return (width, height);
        }
    }
}
