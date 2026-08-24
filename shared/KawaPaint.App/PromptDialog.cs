// KawaPaint - a one-line text prompt (used for renaming layers).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KawaPaint.App;

public sealed class PromptDialog : Window
{
    private readonly TextBox _input;
    public string ResultText => _input.Text ?? "";

    public PromptDialog(string title, string initial)
    {
        Title = title;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _input = new TextBox { Text = initial };

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => Close(true);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        Content = new StackPanel { Margin = new Thickness(16), Children = { _input, buttons } };
        Opened += (_, _) => { _input.SelectAll(); _input.Focus(); };
    }
}
