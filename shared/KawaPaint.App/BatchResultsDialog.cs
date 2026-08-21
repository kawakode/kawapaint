// KawaPaint - a plain read-only summary of a batch script run. One TextBox, same weight as
// ResizeDialog - a results grid isn't worth building for a feature whose output is glanced at
// once per run.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KawaPaint.App;

public sealed class BatchResultsDialog : Window
{
    public BatchResultsDialog(string summary)
    {
        Title = "Batch Apply Results";
        Width = 560;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBox
        {
            Text = summary,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontFamily = "Consolas,Menlo,monospace"
        };
        ScrollViewer.SetVerticalScrollBarVisibility(text, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16) };
        Grid.SetRow(ok, 1);
        ok.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(text);
        root.Children.Add(ok);
        Content = root;
    }
}
