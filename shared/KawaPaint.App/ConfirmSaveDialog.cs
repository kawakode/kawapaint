// KawaPaint - a small "you have unsaved changes" prompt (Save / Discard / Cancel).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KawaPaint.App;

/// <summary>Cancel is first so that dismissing the dialog (window close button) - which yields
/// default(SaveChoice) - is treated as "cancel", not as a silent save.</summary>
public enum SaveChoice { Cancel, Save, Discard }

public sealed class ConfirmSaveDialog : Window
{
    public ConfirmSaveDialog(string message)
    {
        Title = "Unsaved changes";
        Width = 420;
        Height = 150;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var save = new Button { Content = "Save", IsDefault = true };
        save.Click += (_, _) => Close(SaveChoice.Save);
        var discard = new Button { Content = "Discard" };
        discard.Click += (_, _) => Close(SaveChoice.Discard);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(SaveChoice.Cancel);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(discard);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        Content = new StackPanel { Margin = new Thickness(16), Children = { text, buttons } };
    }
}
