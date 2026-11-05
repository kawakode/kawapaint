using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace KawaPaint.App;

/// <summary>
/// In-app dialogs for hosts without windows (browser, Android). Every <see cref="AppDialog"/>
/// shown from this view lands here when there is no desktop window to own it, so the browser
/// runs the very same dialogs as the desktop instead of a reduced copy of each.
/// </summary>
public partial class MainView : IInlineDialogHost
{
    private const double InlineDialogMargin = 12;

    // Top of the stack of in-app dialogs (a dialog can open another, e.g. a prompt over the
    // export preset manager). Null when none is up.
    private Control? _canvasDialogOverlay;
    private readonly List<(Control Shade, AppDialog Dialog)> _inlineDialogs = new();

    /// <summary>Shows dialogs in-app even under a desktop window. Tests use it to exercise the
    /// browser/Android presentation headlessly.</summary>
    public bool PreferInlineDialogs { get; set; }

    /// <summary>Shows <paramref name="dialog"/> over this view: a real modal window on the
    /// desktop, an in-app sheet elsewhere.</summary>
    private Task<T> ShowDialogAsync<T>(AppDialog dialog) => dialog.ShowDialog<T>(this);

    private Task ShowDialogAsync(AppDialog dialog) => dialog.ShowDialog(this);

    /// <summary>Dismisses the topmost in-app dialog as if its close button were pressed. False when none is up.</summary>
    private bool DismissTopInlineDialog()
    {
        if (_inlineDialogs.Count == 0) return false;
        _inlineDialogs[^1].Dialog.RequestClose();
        return true;
    }

    /// <summary>Yes/no confirmation. Cancel is the default button for a destructive action, so
    /// Enter dismisses rather than confirms.</summary>
    private Task<bool> ConfirmAsync(string title, string message, string confirmLabel,
        bool destructive = true) =>
        ShowDialogAsync<bool>(new ConfirmDialog(title, message, confirmLabel, destructive));

    Action IInlineDialogHost.PresentInline(AppDialog dialog)
    {
        var size = dialog.RequestedSize;
        bool fixedHeight = !double.IsNaN(size.Height)
            && dialog.SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight);
        bool fixedWidth = !double.IsNaN(size.Width)
            && dialog.SizeToContent is not (SizeToContent.Width or SizeToContent.WidthAndHeight);

        var title = new TextBlock
        {
            Text = dialog.Title ?? "",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Touch hosts have no Escape key and dialogs like About have no Cancel button.
        var close = new Button
        {
            Content = Icons.Create("Close", 14),
            Padding = new Thickness(8),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => dialog.RequestClose();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);

        // A fixed-height dialog lays its lists out in star rows, which need a finite height to
        // scroll on their own; everything else scrolls as a whole when the viewport is short.
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = fixedHeight ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            Content = dialog
        };
        dialog.MinWidth = 0;
        dialog.MinHeight = 0;
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(scroll);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(InlineDialogMargin),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = body
        };
        var shade = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Focusable = true,
            ZIndex = int.MaxValue,
            Child = card
        };

        // The dialog asked for a window of a given size; give it that much of the view, no more.
        void Fit(Size viewport)
        {
            double chrome = card.Padding.Left + card.Padding.Right + 2;
            double availableWidth = Math.Max(0, viewport.Width - 2 * InlineDialogMargin);
            double availableHeight = Math.Max(0, viewport.Height - 2 * InlineDialogMargin);
            if (fixedWidth) card.Width = Math.Min(size.Width + chrome, availableWidth);
            else
            {
                card.MinWidth = Math.Min(size.MinWidth > 0 ? size.MinWidth + chrome : 0, availableWidth);
                card.MaxWidth = Math.Min(size.MaxWidth + chrome, availableWidth);
            }
            if (fixedHeight) card.Height = Math.Min(size.Height + chrome + 48, availableHeight);
            else card.MaxHeight = availableHeight;
        }
        shade.SizeChanged += (_, e) => Fit(e.NewSize);
        if (FloatingLayer.Bounds.Size is { Width: > 0 } initial) Fit(initial);

        shade.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            dialog.RequestClose();
        };

        var entry = (shade, dialog);
        _inlineDialogs.Add(entry);
        var dismiss = PresentCanvasDialog(shade, () =>
        {
            FindFocusable(dialog)?.Focus();
            dialog.RaiseOpened();
        });
        return () =>
        {
            _inlineDialogs.Remove(entry);
            dismiss();
            scroll.Content = null;
        };
    }

    private Action PresentCanvasDialog(Control shade, Action opened)
    {
        var previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var background = FloatingLayer.Children.Prepend<Control>(RootDock)
            .Select(control => (Control: control, Enabled: control.IsEnabled)).ToArray();
        foreach (var entry in background) entry.Control.IsEnabled = false;
        KeyboardNavigation.SetTabNavigation(shade, KeyboardNavigationMode.Cycle);
        _canvasDialogOverlay = shade;
        FloatingLayer.Children.Add(shade);
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_canvasDialogOverlay, shade)) return;
            shade.Focus();
            opened();
        }, DispatcherPriority.Input);

        return () =>
        {
            FloatingLayer.Children.Remove(shade);
            _canvasDialogOverlay = _inlineDialogs.Count > 0 ? _inlineDialogs[^1].Shade : null;
            foreach (var entry in background) entry.Control.IsEnabled = entry.Enabled;
            if (previousFocus?.Focus() != true) Canvas.Focus();
        };
    }

    private static Control? FindFocusable(Control root)
    {
        if (root is TextBox or NumericUpDown) return root;
        if (root is Panel panel)
            foreach (Control child in panel.Children)
                if (FindFocusable(child) is { } found) return found;
        if (root is ContentControl { Content: Control content }) return FindFocusable(content);
        if (root is Decorator { Child: { } child2 }) return FindFocusable(child2);
        return null;
    }
}
