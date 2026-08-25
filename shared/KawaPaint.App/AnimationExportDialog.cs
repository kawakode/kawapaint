using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KawaPaint.App;

public sealed class AnimationExportDialog : Window
{
    private readonly CheckBox _loop = new() { Content = "Loop forever", IsChecked = true };

    public bool Loop => _loop.IsChecked ?? true;

    public AnimationExportDialog(int frameCount)
    {
        Title = "Export Animation";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var export = new Button { Content = "Export", IsDefault = true };
        export.Click += (_, _) => Close(true);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { cancel, export }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"Export {frameCount} timeline frame(s) using their individual durations." },
                _loop,
                buttons
            }
        };
    }
}
