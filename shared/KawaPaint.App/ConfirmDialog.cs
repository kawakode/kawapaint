// KawaPaint - a plain yes/no confirmation.
//
// The app had no general one: ConfirmSaveDialog is specifically Save/Discard/Cancel and carries
// its own three-way SaveChoice, so a destructive action that just needed "are you sure?" either
// grew a window of its own or (see MainView.OnDeleteLayoutPreset) went ahead without asking.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KawaPaint.App;

public sealed class ConfirmDialog : Window
{
    /// <param name="confirmLabel">Names the action ("Delete") rather than saying "OK", so the
    /// button still reads as what it will do to someone who skimmed the message above it.</param>
    /// <param name="destructive">Makes Cancel the default button, so Enter dismisses rather than
    /// confirms. For anything that discards work, the safe answer should be the one a reflex hits.</param>
    public ConfirmDialog(string title, string message, string confirmLabel = "OK", bool destructive = false)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var confirm = new Button { Content = confirmLabel, IsDefault = !destructive };
        confirm.Click += (_, _) => Close(true);

        // IsCancel, so the window close button and Esc both land here rather than on default(bool)
        // by accident - same reasoning as ConfirmSaveDialog's Cancel-first enum.
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = destructive };
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        Content = new StackPanel { Margin = new Thickness(16), Children = { text, buttons } };
    }
}
