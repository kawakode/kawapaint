// KawaPaint - per-format save options (JPEG quality, WebP lossless/quality), shown when the
// chosen export format actually has options worth asking about.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using KawaPaint.Engine.Codecs;

namespace KawaPaint.App;

public sealed class SaveOptionsDialog : Window
{
    private readonly Slider _quality;
    private readonly CheckBox? _lossless;

    public EncodeOptions ResultOptions => new()
    {
        Quality = (int)_quality.Value,
        Lossless = _lossless?.IsChecked ?? false
    };

    /// <param name="codecId">"jpeg" gets a quality slider; "webp" gets quality + lossless.</param>
    public SaveOptionsDialog(string codecId)
    {
        Title = "Export Options";
        Width = 320;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var content = new StackPanel { Margin = new Thickness(16), Spacing = 8 };

        var qualityLabel = new TextBlock { Text = "Quality: 92" };
        _quality = new Slider { Minimum = 1, Maximum = 100, Value = 92 };
        _quality.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) qualityLabel.Text = $"Quality: {(int)_quality.Value}";
        };
        content.Children.Add(qualityLabel);
        content.Children.Add(_quality);

        if (codecId == "webp")
        {
            _lossless = new CheckBox { Content = "Lossless" };
            _lossless.IsCheckedChanged += (_, _) =>
            {
                bool lossless = _lossless.IsChecked ?? false;
                _quality.IsEnabled = !lossless;
                qualityLabel.IsEnabled = !lossless;
            };
            content.Children.Add(_lossless);
        }

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Content = "Export", IsDefault = true };
        ok.Click += (_, _) => Close(true);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        content.Children.Add(buttons);

        Content = content;
    }
}
