// KawaPaint - prompts for text + size for the Text tool.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KawaPaint.App;

public sealed class TextDialog : Window
{
    private readonly TextBox _text;
    private readonly Slider _size;

    public string ResultText => _text.Text ?? "";
    public int ResultSize => (int)_size.Value;

    public TextDialog()
    {
        Title = "Add Text";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            PlaceholderText = "Type text…"
        };

        _size = new Slider { Minimum = 8, Maximum = 240, Value = 48 };
        var sizeValue = new TextBlock { Text = "48 px", VerticalAlignment = VerticalAlignment.Center, Width = 52 };
        _size.ValueChanged += (_, e) => sizeValue.Text = (int)e.NewValue + " px";

        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizeRow.Children.Add(new TextBlock { Text = "Size", VerticalAlignment = VerticalAlignment.Center, Width = 40 });
        _size.Width = 240;
        sizeRow.Children.Add(_size);
        sizeRow.Children.Add(sizeValue);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => Close(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        root.Children.Add(_text);
        root.Children.Add(sizeRow);
        root.Children.Add(buttons);
        Content = root;

        Opened += (_, _) => _text.Focus();
    }
}
