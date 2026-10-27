// KawaPaint - the touch interfaces. The tablet one is the desktop arrangement with targets big
// enough for a finger; the phone one gives the canvas the whole width, puts the tools in a strip
// along the bottom and opens every other panel as a bottom sheet.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using KawaPaint.App.Core;

namespace KawaPaint.App;

public partial class MainView
{
    private FormFactor _formFactor;

    public FormFactor CurrentFormFactor => _formFactor;

    /// <summary>Tool buttons: compact for a mouse, about 7 mm on a touch screen.</summary>
    private double ToolButtonSize => _formFactor == FormFactor.Desktop ? 28 : 40;
    private double ToolButtonPadding => _formFactor == FormFactor.Desktop ? 3 : 8;

    private string? FormFactorLayoutName => _formFactor switch
    {
        FormFactor.Tablet => "Tablet",
        FormFactor.Phone => "Phone",
        _ => null
    };

    // "Mobile" is what the single Android layout was called before tablets and phones split.
    private static bool IsFormFactorLayoutName(string name) => name is "Tablet" or "Phone" or "Mobile";

    private void InitializeFormFactor()
    {
        var workspace = _settings.Settings.Workspace;
        _formFactor = DeviceInfo.Resolve(workspace.Interface);
        foreach (var item in InterfaceMenu.Items.OfType<MenuItem>())
            item.IsChecked = string.Equals(item.Tag as string, workspace.Interface, StringComparison.OrdinalIgnoreCase);
        if (_formFactor == FormFactor.Desktop) return;

        Classes.Add("touch");
        Classes.Add(_formFactor == FormFactor.Phone ? "phone" : "tablet");

        // Undo, redo and the panel toggles are the only buttons a touch user reaches all the
        // time, and at 26 px they are a near miss for a fingertip.
        foreach (var button in this.GetLogicalDescendants().OfType<TemplatedControl>()
                     .Where(c => c.Classes.Contains("iconbtn") || c.Classes.Contains("paneltoggle")))
        {
            button.Width = 40;
            button.Height = 40;
            button.Padding = new Thickness(9);
        }

        if (_formFactor == FormFactor.Phone) ApplyPhoneChrome();
    }

    private void ApplyPhoneChrome()
    {
        // One menu button instead of nine top-level menus that would wrap over three rows.
        var menus = MainMenu.Items.OfType<MenuItem>().ToList();
        MainMenu.Items.Clear();
        var root = new MenuItem
        {
            Header = new TextBlock { Text = "☰", FontSize = 20, VerticalAlignment = VerticalAlignment.Center },
            MinWidth = 44,
            MinHeight = 40
        };
        root.Classes.Add("topbar");
        ToolTip.SetTip(root, "Menu");
        foreach (var menu in menus)
        {
            menu.Classes.Remove("topbar");
            root.Items.Add(menu);
        }
        MainMenu.Items.Add(root);

        // The tool strip is always on screen, and the rest are in View > Panels.
        ToggleTools.IsVisible = false;
        ToggleTimeline.IsVisible = false;
        ToggleDock.IsVisible = false;
        TogglePaint3D.IsVisible = false;

        // Tool options on one line that scrolls sideways, rather than wrapping into rows that
        // push the canvas down.
        OptionsBar.Child = null;
        OptionsBar.Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = OptionsPanel
        };
        OptionsBar.Padding = new Thickness(8, 2);

        // Tools in a strip along the bottom, within reach of the thumb.
        ToolPalette.Orientation = Orientation.Horizontal;
        ToolScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        ToolScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ToolsHeader.IsVisible = false;
        ToolsBorder.BorderThickness = new Thickness(0, 1, 0, 0);

        CoordText.IsVisible = false;
    }

    /// <summary>The rule between two tool groups: across the vertical rail, down the phone strip.</summary>
    private Border ToolGroupSeparator() => _formFactor == FormFactor.Phone
        ? new Border
        {
            Width = 1,
            Margin = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.DimGray
        }
        : new Border
        {
            Height = 1,
            Margin = new Thickness(2, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.DimGray
        };

    /// <summary>Where each panel starts out in a fresh tablet or phone layout.</summary>
    private void ApplyFormFactorLayout()
    {
        if (_formFactor == FormFactor.Desktop) return;
        switch (_formFactor)
        {
            case FormFactor.Tablet:
                // Two columns of finger-sized tools, then colors and layers down the right.
                Place("Tools", PanelPlace.Left, dockSize: 2 * (ToolButtonSize + 2) + 14);
                Place("ColorWheel", PanelPlace.Right, dockSize: 230);
                Place("Layers", PanelPlace.Right, dockSize: 260);
                Place("Colors", PanelPlace.Bottom);
                Place("History", PanelPlace.Hidden, floatWidth: 320, floatHeight: 520);
                Place("Timeline", PanelPlace.Hidden, floatWidth: 640, floatHeight: 260);
                Place("Dock", PanelPlace.Hidden, floatWidth: 320, floatHeight: 220);
                Place("Paint3D", PanelPlace.Hidden, floatWidth: 360, floatHeight: 460);
                break;

            case FormFactor.Phone:
                Place("Tools", PanelPlace.Bottom, dockSize: ToolButtonSize + 16);
                foreach (var id in new[] { "Colors", "ColorWheel", "Layers", "History", "Timeline", "Dock", "Paint3D" })
                    Place(id, PanelPlace.Hidden, shown: PanelPlace.Sheet);
                break;
        }

        // Rulers cost a finger's width of canvas on each edge and guides are fiddly by touch;
        // View > Rulers brings them back.
        _settings.Settings.Workspace.ShowRulers = false;

        void Place(string id, PanelPlace place, double dockSize = double.NaN, PanelPlace? shown = null,
            double floatWidth = double.NaN, double floatHeight = double.NaN)
        {
            var placement = _panels.PlacementOf(id);
            placement.Place = place;
            placement.LastShown = shown ?? (place == PanelPlace.Hidden ? PanelPlace.Floating : place);
            placement.LastDock = place is PanelPlace.Left or PanelPlace.Right or PanelPlace.Top or PanelPlace.Bottom
                ? place
                : id == "Tools" ? PanelPlace.Left : PanelPlace.Right;
            placement.DockSize = dockSize;
            placement.FloatX = 40;
            placement.FloatY = 90;
            placement.FloatWidth = floatWidth;
            placement.FloatHeight = floatHeight;
        }
    }

    private void OnSetInterface(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string choice }) return;
        var workspace = _settings.Settings.Workspace;
        workspace.Interface = choice;
        _settings.Save();
        foreach (var item in InterfaceMenu.Items.OfType<MenuItem>())
            item.IsChecked = ReferenceEquals(item, sender);

        var next = DeviceInfo.Resolve(choice);
        StatusText.Text = next == _formFactor
            ? $"Interface: {next}."
            : $"The {next.ToString().ToLowerInvariant()} interface will be used from the next start.";
    }
}
