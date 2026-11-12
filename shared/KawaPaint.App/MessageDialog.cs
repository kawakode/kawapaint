// KawaPaint - tells the user something went wrong, in a place they cannot miss.
//
// A failed open, save or export used to be reported only in the status bar: one line at the
// bottom of the window, overwritten by the next message. Someone who pressed Save and looked
// away had no reason to think the file was not written.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KawaPaint.App;

public sealed class MessageDialog : AppDialog
{
    public MessageDialog(string title, string message)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        // Default and cancel at once: Enter, Esc and the close button all dismiss it.
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();

        Content = new StackPanel { Margin = new Thickness(16), Children = { text, ok } };
    }
}
