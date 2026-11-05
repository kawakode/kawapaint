// KawaPaint - one dialog type for every host. Avalonia's browser and Android hosts have no
// windowing platform: merely constructing a Window there throws "Browser doesn't support
// windowing platform", so a dialog that derived from Window broke the moment its menu item was
// clicked (every adjustment, Curves, Import 3D Model...). An AppDialog is a plain control instead:
// on the desktop it is shown inside a real modal Window built on demand, and on single-view hosts
// the nearest IInlineDialogHost (MainView) shows it as an in-app modal sheet. The dialogs keep the
// Window-shaped API they were written against (Title, ShowDialog, Close(result), Opened/Closed).

using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace KawaPaint.App;

/// <summary>Shows dialogs inside the app's own view where there are no windows.</summary>
public interface IInlineDialogHost
{
    /// <summary>True to show dialogs inline even under a desktop window (tests, kiosk-style hosts).</summary>
    bool PreferInlineDialogs { get; }

    /// <summary>Puts <paramref name="dialog"/> on screen modally and returns the action that takes
    /// it down again. The host calls <see cref="AppDialog.RaiseOpened"/> once it is up and
    /// <see cref="AppDialog.RequestClose"/> when the user dismisses it (Escape, back, the close button).</summary>
    Action PresentInline(AppDialog dialog);
}

/// <summary>Cancellable notice that a dialog is about to close.</summary>
public sealed class DialogClosingEventArgs(bool isProgrammatic) : CancelEventArgs
{
    /// <summary>True when code called Close; false when the user dismissed the dialog.</summary>
    public bool IsProgrammatic { get; } = isProgrammatic;
}

public class AppDialog : ContentControl
{
    private Window? _window;
    private TaskCompletionSource<object?>? _inline;
    private Action? _dismissInline;

    protected override Type StyleKeyOverride => typeof(ContentControl);

    public string? Title { get; set; }
    public WindowStartupLocation WindowStartupLocation { get; set; } = WindowStartupLocation.CenterOwner;
    public bool CanResize { get; set; } = true;
    public SizeToContent SizeToContent { get; set; }
    public WindowIcon? Icon { get; set; }

    /// <summary>The dialog's requested size, as given through Width/Height/Min*/Max* before it was
    /// shown. Those describe the whole dialog, so the host applies them to its frame.</summary>
    internal (double Width, double Height, double MinWidth, double MinHeight, double MaxWidth, double MaxHeight) RequestedSize { get; private set; }

    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<DialogClosingEventArgs>? Closing;

    /// <summary>True from ShowDialog until the dialog has closed.</summary>
    public bool IsOpen => _window is not null || _inline is not null;

    /// <summary>The storage provider of whatever is hosting the dialog right now.</summary>
    public IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)?.StorageProvider
        ?? throw new InvalidOperationException("The dialog is not on screen");

    public Task ShowDialog(Visual owner) => ShowDialog<object?>(owner);

    /// <summary>Shows the dialog modally over <paramref name="owner"/> (a window, a view, or another
    /// dialog) and completes with the value passed to <see cref="Close(object?)"/>, or default when
    /// it was dismissed.</summary>
    public async Task<T> ShowDialog<T>(Visual owner)
    {
        if (IsOpen) throw new InvalidOperationException("The dialog is already open");
        TakeRequestedSize();
        IsVisible = true;

        IInlineDialogHost? host = owner.GetSelfAndVisualAncestors().OfType<IInlineDialogHost>().FirstOrDefault();
        object? result;
        if (TopLevel.GetTopLevel(owner) is Window ownerWindow && host?.PreferInlineDialogs != true)
        {
            result = await ShowInWindowAsync(ownerWindow);
        }
        else
        {
            if (host is null) throw new InvalidOperationException("Nothing can host a dialog here");
            _inline = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<object?> completion = _inline.Task;
            _dismissInline = host.PresentInline(this);
            result = await completion;
        }
        return result is T value ? value : default!;
    }

    public void Close() => Close(null);

    public void Close(object? dialogResult) => CloseCore(dialogResult, isProgrammatic: true);

    /// <summary>The user dismissed the dialog through its host (Escape, back, the close button) -
    /// the inline counterpart of a window's close button.</summary>
    public void RequestClose() => CloseCore(null, isProgrammatic: false);

    /// <summary>Called by an inline host once the dialog is on screen.</summary>
    public void RaiseOpened() => Opened?.Invoke(this, EventArgs.Empty);

    private void CloseCore(object? dialogResult, bool isProgrammatic)
    {
        if (_window is { } window)
        {
            if (isProgrammatic) window.Close(dialogResult);
            else window.Close();
            return;
        }
        if (_inline is not { } inline) return;

        var closing = new DialogClosingEventArgs(isProgrammatic);
        Closing?.Invoke(this, closing);
        if (closing.Cancel) return;

        _inline = null;
        _dismissInline?.Invoke();
        _dismissInline = null;
        IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
        inline.TrySetResult(dialogResult);
    }

    private async Task<object?> ShowInWindowAsync(Window owner)
    {
        var size = RequestedSize;
        var window = new Window
        {
            Title = Title,
            WindowStartupLocation = WindowStartupLocation,
            CanResize = CanResize,
            SizeToContent = SizeToContent,
            Width = size.Width,
            Height = size.Height,
            MinWidth = size.MinWidth,
            MinHeight = size.MinHeight,
            MaxWidth = size.MaxWidth,
            MaxHeight = size.MaxHeight,
            Content = this
        };
        if (Icon is not null) window.Icon = Icon;
        window.Opened += (_, _) => Opened?.Invoke(this, EventArgs.Empty);
        window.Closing += (_, e) =>
        {
            var closing = new DialogClosingEventArgs(e.IsProgrammatic);
            Closing?.Invoke(this, closing);
            e.Cancel = closing.Cancel;
        };
        window.Closed += (_, _) =>
        {
            _window = null;
            window.Content = null;
            IsVisible = false;
            Closed?.Invoke(this, EventArgs.Empty);
        };
        _window = window;
        return await window.ShowDialog<object?>(owner);
    }

    /// <summary>Width/Height &amp; co. were set by the dialog to size its window; move them to the
    /// requested size so the dialog's content can follow its frame instead of pinning it.</summary>
    private void TakeRequestedSize()
    {
        if (!double.IsNaN(Width) || !double.IsNaN(Height) || MinWidth > 0 || MinHeight > 0
            || !double.IsPositiveInfinity(MaxWidth) || !double.IsPositiveInfinity(MaxHeight))
        {
            RequestedSize = (Width, Height, MinWidth, MinHeight, MaxWidth, MaxHeight);
            Width = Height = double.NaN;
            MinWidth = MinHeight = 0;
            MaxWidth = MaxHeight = double.PositiveInfinity;
        }
        else if (RequestedSize == default)
        {
            RequestedSize = (double.NaN, double.NaN, 0, 0, double.PositiveInfinity, double.PositiveInfinity);
        }
    }
}
