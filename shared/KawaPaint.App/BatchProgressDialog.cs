// KawaPaint - progress and Cancel for long batch runs (Batch Apply Script, Mail Merge). The work
// itself runs off the UI thread; this window only shows how far it got and turns Cancel, or
// closing the window, into a cancellation request that the run checks between files.

using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KawaPaint.App;

public sealed class BatchProgressDialog : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly Button _cancelButton;
    private readonly int _total;
    private bool _finished;

    public BatchProgressDialog(string title, int total)
    {
        _total = total;
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _status = new TextBlock { Text = "Starting…", TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        _bar = new ProgressBar { Minimum = 0, Maximum = total, Height = 16, Margin = new Thickness(0, 8, 0, 0) };
        _cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _cancelButton.Click += (_, _) => RequestCancel();

        Content = new StackPanel { Margin = new Thickness(16), Children = { _status, _bar, _cancelButton } };
        Closing += (_, e) =>
        {
            if (_finished) return;
            // Closing early means "stop"; the window goes away once the current file is done.
            RequestCancel();
            e.Cancel = true;
        };
    }

    public CancellationToken Token => _cancel.Token;

    /// <summary>Shows that <paramref name="done"/> items are finished and <paramref name="current"/>
    /// is next. Safe to call from any thread.</summary>
    public void Report(int done, string current) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _bar.Value = done;
        if (!_cancel.IsCancellationRequested) _status.Text = $"{done + 1} of {_total}: {current}";
    });

    /// <summary>Closes the window once the run has stopped, whether it finished or was cancelled.</summary>
    public void Finish()
    {
        _finished = true;
        Close();
        _cancel.Dispose();
    }

    private void RequestCancel()
    {
        if (_cancel.IsCancellationRequested) return;
        _cancel.Cancel();
        _status.Text = "Cancelling after the current file…";
        _cancelButton.IsEnabled = false;
    }
}
