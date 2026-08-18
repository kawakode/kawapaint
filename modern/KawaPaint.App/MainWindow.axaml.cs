using System.Threading.Tasks;
using Avalonia.Controls;

namespace KawaPaint.App;

/// <summary>Desktop window shell around <see cref="MainView"/>, which holds all the real UI logic.
/// The browser head hosts <see cref="MainView"/> directly (Avalonia's browser target has no
/// Window/popup support), so anything Window-specific — the title bar text, close confirmation —
/// lives here instead of in MainView.</summary>
public partial class MainWindow : Window
{
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
        View.TitleChanged += t => Title = t;
        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose || !View.IsDirty) return;
        e.Cancel = true;
        _ = HandleCloseAsync();
    }

    private async Task HandleCloseAsync()
    {
        if (await View.ConfirmDiscardAsync()) { _forceClose = true; Close(); }
    }
}
