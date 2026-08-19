// KawaPaint — a minimal About/Credits dialog, mirroring README.md's Credits section.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace KawaPaint.App;

public sealed class AboutDialog : Window
{
    public AboutDialog()
    {
        Title = "About KawaPaint";
        Width = 440;
        Height = 460;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        TextBlock Header(string text) => new()
        {
            Text = text,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 12, 0, 4)
        };

        TextBlock Body(string text) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };

        var content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "KawaPaint", FontSize = 20, FontWeight = FontWeight.Bold },
                Body("A Paint.NET-inspired image editor, maintained by Kawa."),

                Header("Fork"),
                Body("Based on Paint.NET 3.36 (the last MIT-licensed release) by Rick Brewster and contributors. See FORK.TXT for fork rationale."),

                Header("Libraries"),
                Body("SkiaSharp — 2D graphics rendering"),
                Body("Avalonia, Avalonia.Controls.ColorPicker, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter, Avalonia.Browser — cross-platform UI framework"),

                Header("Icons"),
                Body("Toolbox, panel, and menu icons are adapted from Lucide (lucide.dev), ISC License (with portions under the MIT-licensed Feather Icons this project forked from)."),

                Header("License"),
                Body("KawaPaint is licensed under the MIT License, consistent with Paint.NET 3.36. See LICENSE.MD for details.")
            }
        };

        var scroller = new ScrollViewer
        {
            Content = content,
            Height = 380,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children = { scroller, close }
        };
    }
}
