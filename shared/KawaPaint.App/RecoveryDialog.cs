// KawaPaint - offers a leftover autosave snapshot back to the user at startup.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KawaPaint.App;

public enum RecoveryChoice { Discard, Restore }

public sealed class RecoveryDialog : Window
{
    public RecoveryDialog(int count)
    {
        Title = "Recover unsaved work?";
        Width = 420;
        Height = 150;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        string noun = count == 1 ? "document" : "documents";
        var text = new TextBlock
        {
            Text = $"KawaPaint found autosaved work from a previous session ({count} {noun}). Restore it?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var restore = new Button { Content = "Restore", IsDefault = true };
        restore.Click += (_, _) => Close(RecoveryChoice.Restore);
        var discard = new Button { Content = "Discard", IsCancel = true };
        discard.Click += (_, _) => Close(RecoveryChoice.Discard);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(discard);
        buttons.Children.Add(restore);

        Content = new StackPanel { Margin = new Thickness(16), Children = { text, buttons } };
    }
}
