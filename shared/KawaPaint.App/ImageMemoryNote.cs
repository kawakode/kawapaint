// KawaPaint - the line under a size dialog's width/height that says how much memory the image
// will take, warns when that is a large share of RAM, and disables OK when it would not fit.

using Avalonia.Controls;
using Avalonia.Media;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class ImageMemoryNote
{
    private readonly NumericUpDown _width;
    private readonly NumericUpDown _height;
    private readonly Button _ok;
    private readonly int _layers;
    private readonly long _previousBytes;

    public TextBlock Text { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    /// <param name="currentWidth">Size of the document being changed, or 0 for a new image.</param>
    public ImageMemoryNote(NumericUpDown width, NumericUpDown height, Button ok, int layers,
        int currentWidth = 0, int currentHeight = 0)
    {
        _width = width;
        _height = height;
        _ok = ok;
        _layers = layers;
        _previousBytes = (long)currentWidth * currentHeight * 4 * layers;
        width.ValueChanged += (_, _) => Update();
        height.ValueChanged += (_, _) => Update();
        Update();
    }

    private void Update()
    {
        int w = (int)(_width.Value ?? 1), h = (int)(_height.Value ?? 1);
        long bytes = ImageMemory.Estimate(w, h, _layers, _previousBytes);
        long budget = ImageMemory.Budget;
        switch (ImageMemory.Check(bytes, budget))
        {
            case ImageMemoryFit.TooLarge:
                Text.Text = $"Needs about {ImageMemory.Format(bytes)}, more than this computer can provide ({ImageMemory.Format(budget)}). Choose a smaller size.";
                Text.Foreground = Brushes.IndianRed;
                _ok.IsEnabled = false;
                break;
            case ImageMemoryFit.Tight:
                Text.Text = $"Needs about {ImageMemory.Format(bytes)} of {ImageMemory.Format(budget)}. The editor may become slow or run out of memory.";
                Text.Foreground = Brushes.DarkOrange;
                _ok.IsEnabled = true;
                break;
            default:
                Text.Text = $"Memory: about {ImageMemory.Format(bytes)}";
                Text.ClearValue(TextBlock.ForegroundProperty);
                _ok.IsEnabled = true;
                break;
        }
    }
}
