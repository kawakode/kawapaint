using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KawaPaint.Engine;

namespace KawaPaint.App;

public partial class MainView
{
    private Control? _canvasDialogOverlay;

    private sealed record NewImageValues(int Width, int Height, double Dpi, bool Transparent);
    private sealed record SizeValues(int Width, int Height);
    private sealed record CanvasSizeValues(int Width, int Height, CanvasAnchor Anchor);
    private sealed record TextValues(string Text, int Size);
    private sealed record AnimationValues(int DelayMs, bool Loop);
    private readonly record struct CanvasChoice<T>(string Label, T Value, bool IsDefault = false);

    /// <summary>Single-view/browser replacement for Window.ShowDialog. The shade is added last to
    /// FloatingLayer, so it intercepts input over both the editor and floating panels.</summary>
    private Task<T> ShowCanvasChoiceAsync<T>(string title, Control body, T cancelValue,
        params CanvasChoice<T>[] choices)
    {
        if (_canvasDialogOverlay is not null) return Task.FromResult(cancelValue);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        });
        content.Children.Add(body);
        content.Children.Add(buttons);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(18),
            MinWidth = 320,
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };
        var shade = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Focusable = true,
            Child = card
        };
        shade.ZIndex = int.MaxValue;

        void Complete(T value)
        {
            if (!completion.TrySetResult(value)) return;
            FloatingLayer.Children.Remove(shade);
            _canvasDialogOverlay = null;
            Canvas.Focus();
        }

        foreach (var choice in choices)
        {
            var button = new Button { Content = choice.Label, IsDefault = choice.IsDefault };
            T value = choice.Value;
            button.Click += (_, _) => Complete(value);
            buttons.Children.Add(button);
        }

        shade.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Complete(cancelValue);
        };

        _canvasDialogOverlay = shade;
        FloatingLayer.Children.Add(shade);
        Dispatcher.UIThread.Post(() =>
        {
            shade.Focus();
            FindFocusable(body)?.Focus();
        }, DispatcherPriority.Input);
        return completion.Task;
    }

    private async Task<(bool Accepted, T Value)> ShowCanvasFormAsync<T>(string title, Control body,
        Func<T> capture)
    {
        object cancel = new();
        object accept = new();
        object result = await ShowCanvasChoiceAsync(title, body, cancel,
            new CanvasChoice<object>("Cancel", cancel),
            new CanvasChoice<object>("OK", accept, true));
        return ReferenceEquals(result, accept) ? (true, capture()) : (false, default!);
    }

    /// <summary>Hosts the already-built content of one of the desktop dialogs. The dialog's own
    /// buttons signal <paramref name="connectClose"/>, so preview/commit/revert stays in that one
    /// implementation instead of being copied into a browser-only version.</summary>
    private Task<bool> ShowCanvasWindowContentAsync(Window window, Action<Action<bool>> connectClose,
        Action cancel, Action opened)
    {
        if (_canvasDialogOverlay is not null) return Task.FromResult(false);
        if (window.Content is not Control dialogBody) return Task.FromResult(false);
        window.Content = null;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = window.Title ?? "Dialog",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        });
        content.Children.Add(dialogBody);

        var shade = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Focusable = true,
            ZIndex = int.MaxValue,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = content
            }
        };

        void Complete(bool result)
        {
            if (!completion.TrySetResult(result)) return;
            FloatingLayer.Children.Remove(shade);
            _canvasDialogOverlay = null;
            Canvas.Focus();
        }

        connectClose(Complete);
        shade.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            cancel();
            Complete(false);
        };

        _canvasDialogOverlay = shade;
        FloatingLayer.Children.Add(shade);
        Dispatcher.UIThread.Post(() => { shade.Focus(); opened(); }, DispatcherPriority.Input);
        return completion.Task;
    }

    private async Task<string?> ShowCanvasPromptAsync(string title, string initial)
    {
        var input = new TextBox { Text = initial, MinWidth = 280 };
        var result = await ShowCanvasFormAsync(title, input, () => input.Text ?? "");
        return result.Accepted ? result.Value : null;
    }

    private Task<NewImageValues?> ShowCanvasNewImageAsync()
    {
        var width = Number(800, 1, 20_000);
        var height = Number(600, 1, 20_000);
        var dpi = Number(96, 1, 2_400);
        var transparent = new CheckBox { Content = "Transparent background" };
        var body = Form(("Width", width), ("Height", height), ("DPI", dpi));
        body.Children.Add(transparent);
        return NullableForm("New Image", body, () => new NewImageValues(
            (int)(width.Value ?? 1), (int)(height.Value ?? 1), (double)(dpi.Value ?? 96),
            transparent.IsChecked ?? false));
    }

    private Task<SizeValues?> ShowCanvasSizeFormAsync(string title, int currentWidth, int currentHeight)
    {
        var width = Number(currentWidth, 1, 20_000);
        var height = Number(currentHeight, 1, 20_000);
        return NullableForm(title, Form(("Width", width), ("Height", height)),
            () => new SizeValues((int)(width.Value ?? 1), (int)(height.Value ?? 1)));
    }

    private Task<CanvasSizeValues?> ShowCanvasCanvasSizeAsync(int currentWidth, int currentHeight)
    {
        var width = Number(currentWidth, 1, 20_000);
        var height = Number(currentHeight, 1, 20_000);
        CanvasAnchor selected = CanvasAnchor.MiddleCenter;
        var anchors = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("36,36,36"),
            RowDefinitions = new RowDefinitions("36,36,36"),
            ColumnSpacing = 4,
            RowSpacing = 4,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        CanvasAnchor[] values = Enum.GetValues<CanvasAnchor>();
        var anchorButtons = new List<Button>(9);
        for (int i = 0; i < 9; i++)
        {
            CanvasAnchor value = values[i];
            var button = new Button { Content = "•", Padding = new Thickness(0) };
            button.Click += (_, _) =>
            {
                selected = value;
                foreach (var candidate in anchorButtons)
                    candidate.BorderBrush = ReferenceEquals(candidate, button) ? Brushes.DodgerBlue : Brushes.Gray;
            };
            Grid.SetRow(button, i / 3);
            Grid.SetColumn(button, i % 3);
            anchors.Children.Add(button);
            anchorButtons.Add(button);
        }
        anchorButtons[4].BorderBrush = Brushes.DodgerBlue;

        var body = Form(("Width", width), ("Height", height));
        body.Children.Add(new TextBlock { Text = "Anchor", Margin = new Thickness(0, 6, 0, 0) });
        body.Children.Add(anchors);
        return NullableForm("Canvas Size", body, () => new CanvasSizeValues(
            (int)(width.Value ?? 1), (int)(height.Value ?? 1), selected));
    }

    private Task<TextValues?> ShowCanvasTextAsync()
    {
        var text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 100,
            MinWidth = 360,
            PlaceholderText = "Type text…"
        };
        var size = Number(48, 8, 240);
        var body = new StackPanel { Spacing = 8, Children = { text, Form(("Size", size)) } };
        return NullableForm("Add Text", body, () => new TextValues(text.Text ?? "", (int)(size.Value ?? 48)));
    }

    private Task<AnimationValues?> ShowCanvasAnimationSettingsAsync(int frameCount)
    {
        var delay = Number(100, 10, 60_000);
        delay.Increment = 10;
        var loop = new CheckBox { Content = "Loop forever", IsChecked = true };
        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"Each of the {frameCount} visible layers becomes one frame." },
                Form(("Delay (ms)", delay)),
                loop
            }
        };
        return NullableForm("Export Animated GIF", body,
            () => new AnimationValues((int)(delay.Value ?? 100), loop.IsChecked ?? true));
    }

    private async Task<T?> NullableForm<T>(string title, Control body, Func<T> capture) where T : class
    {
        var result = await ShowCanvasFormAsync(title, body, capture);
        return result.Accepted ? result.Value : null;
    }

    private static NumericUpDown Number(decimal value, decimal minimum, decimal maximum) => new()
    {
        Value = value,
        Minimum = minimum,
        Maximum = maximum,
        Increment = 1
    };

    private static StackPanel Form(params (string Label, Control Input)[] rows)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var (label, input) in rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), ColumnSpacing = 8 };
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(input, 1);
            grid.Children.Add(input);
            panel.Children.Add(grid);
        }
        return panel;
    }

    private static Control? FindFocusable(Control root)
    {
        if (root is TextBox or NumericUpDown) return root;
        if (root is Panel panel)
            foreach (Control child in panel.Children)
                if (FindFocusable(child) is { } found) return found;
        if (root is ContentControl { Content: Control content }) return FindFocusable(content);
        return null;
    }
}
