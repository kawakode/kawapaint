using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using KawaPaint.App.Core;

namespace KawaPaint.App;

/// <summary>Desktop window shell around <see cref="MainView"/>, which holds all the real UI logic.
/// The browser head hosts <see cref="MainView"/> directly (Avalonia's browser target has no
/// Window/popup support), so anything Window-specific - the title bar text, close confirmation,
/// the saved window geometry - lives here instead of in MainView.</summary>
public partial class MainWindow : Window
{
    private bool _forceClose;

    /// <summary>
    /// Last geometry seen while the window was in its normal state. Maximizing replaces Position
    /// and ClientSize with the screen's, so reading them at close time would persist the maximized
    /// rect as the restored one and the window would never come back to its old size.
    /// </summary>
    private PixelPoint _normalPosition;
    private Size _normalSize;
    private bool _capturePending;

    public MainWindow()
    {
        InitializeComponent();
        View.TitleChanged += t => Title = t;

        RestoreGeometry();

        PositionChanged += (_, _) => CaptureNormalGeometryLater();
        Resized += (_, _) => CaptureNormalGeometryLater();

        Closing += OnWindowClosing;
    }

    // ---- geometry persistence ---------------------------------------------

    /// <summary>
    /// Records the current geometry as the restored one - but on a later dispatcher pass, not
    /// inline. Maximizing raises Resized and PositionChanged with the maximized rect *before*
    /// WindowState has flipped to Maximized, so sampling inline records full-screen bounds as the
    /// restored ones and un-maximizing later lands at the wrong size (observed: a window last used
    /// at 980x640 saved -8,-8 1920x1027). By the time a posted callback runs, WindowState agrees
    /// with what the window actually is.
    /// </summary>
    private void CaptureNormalGeometryLater()
    {
        if (_capturePending) return;   // coalesce the burst a resize drag produces
        _capturePending = true;

        Dispatcher.UIThread.Post(() =>
        {
            _capturePending = false;
            if (WindowState != WindowState.Normal) return;
            _normalPosition = Position;
            _normalSize = ClientSize;
        }, DispatcherPriority.Background);
    }

    private void RestoreGeometry()
    {
        var saved = SettingsService.Instance.Settings.Workspace.Window;
        if (saved is null || saved.Width < MinRestorableSize || saved.Height < MinRestorableSize)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _normalSize = new Size(Width, Height);   // the AXAML default; Position isn't meaningful yet
            return;
        }

        Width = saved.Width;
        Height = saved.Height;

        // Seed the tracked geometry from what was saved rather than from the live properties: a
        // window restored straight into Maximized never passes through a Normal state to sample,
        // and Position before the window is shown is not yet its real one.
        _normalPosition = new PixelPoint((int)saved.X, (int)saved.Y);
        _normalSize = new Size(saved.Width, saved.Height);

        // A saved position is only usable if some screen still covers it - a window restored onto
        // a monitor that has since been unplugged (or a resolution that shrank) would open
        // off-screen with no way to reach its title bar. Fall back to centering instead.
        var target = new PixelRect((int)saved.X, (int)saved.Y, (int)saved.Width, (int)saved.Height);
        if (IsOnAScreen(target))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = target.Position;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>True when enough of <paramref name="rect"/> overlaps a connected screen's working
    /// area to grab the title bar. Screens is unavailable on some backends before the window is
    /// shown, in which case we decline to place it by hand rather than guessing.</summary>
    private bool IsOnAScreen(PixelRect rect)
    {
        try
        {
            var all = Screens?.All;
            if (all is null || all.Count == 0) return false;

            return all.Any(screen =>
            {
                var overlap = screen.WorkingArea.Intersect(rect);
                return overlap.Width >= MinVisibleOnScreen && overlap.Height >= MinVisibleOnScreen;
            });
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Guards against a corrupt or hand-edited settings file collapsing the window.</summary>
    private const double MinRestorableSize = 200;

    /// <summary>How much of the window has to land on a screen for the position to be worth reusing.</summary>
    private const int MinVisibleOnScreen = 120;

    private void SaveGeometry()
    {
        SettingsService.Instance.Update(s => s.Workspace.Window = new WindowGeometry
        {
            X = _normalPosition.X,
            Y = _normalPosition.Y,
            Width = _normalSize.Width,
            Height = _normalSize.Height,
            Maximized = WindowState == WindowState.Maximized
        });
    }

    // ---- close ------------------------------------------------------------

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Before the dirty check: this runs again on the second pass when the user chose to save,
        // and writing it here covers both that path and a straight close.
        SaveGeometry();

        if (_forceClose || !View.IsDirty) return;
        e.Cancel = true;
        _ = HandleCloseAsync();
    }

    private async Task HandleCloseAsync()
    {
        if (await View.ConfirmDiscardAsync()) { _forceClose = true; Close(); }
    }
}
