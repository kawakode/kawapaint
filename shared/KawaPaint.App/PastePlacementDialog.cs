// KawaPaint - asks how to place a pasted image that doesn't fit the current canvas.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KawaPaint.App;

/// <summary>Cancel is first so that dismissing the dialog (window close button) - which yields
/// default(PastePlacement) - is treated as "cancel", not as a silent paste.</summary>
public enum PastePlacement { Cancel, GrowCanvas, ScaleToFit, PasteAsIs }

public sealed class PastePlacementDialog : Window
{
    public PastePlacementDialog(int canvasWidth, int canvasHeight, int imageWidth, int imageHeight)
    {
        Title = "Paste";
        Width = 480;
        Height = 170;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBlock
        {
            Text = $"The pasted image ({imageWidth}×{imageHeight}) doesn't fit the canvas " +
                   $"({canvasWidth}×{canvasHeight}). What would you like to do?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var asIs = new Button { Content = "Paste As Is" };
        asIs.Click += (_, _) => Close(PastePlacement.PasteAsIs);
        var scale = new Button { Content = "Scale to Fit" };
        scale.Click += (_, _) => Close(PastePlacement.ScaleToFit);
        var grow = new Button { Content = "Grow Canvas", IsDefault = true };
        grow.Click += (_, _) => Close(PastePlacement.GrowCanvas);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(PastePlacement.Cancel);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(asIs);
        buttons.Children.Add(scale);
        buttons.Children.Add(grow);

        Content = new StackPanel { Margin = new Thickness(16), Children = { text, buttons } };
    }
}
