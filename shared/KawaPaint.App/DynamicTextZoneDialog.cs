using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using KawaPaint.Engine.MailMerge;

namespace KawaPaint.App;

public sealed class DynamicTextZoneDialog : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _template = new() { AcceptsReturn = true, MinHeight = 55 };
    private readonly NumericUpDown _x = Number(0, 100000), _y = Number(0, 100000);
    private readonly NumericUpDown _width = Number(1, 100000), _height = Number(1, 100000);
    private readonly NumericUpDown _size = Number(6, 1000);
    private readonly TextBox _font = new() { PlaceholderText = "Default font" };
    private readonly TextBox _color = new();
    private readonly ComboBox _align = new() { ItemsSource = Enum.GetValues<DynamicTextAlignment>() };
    private readonly ComboBox _vertical = new() { ItemsSource = Enum.GetValues<DynamicTextVerticalAlignment>() };
    private readonly CheckBox _wrap = new() { Content = "Wrap text" };
    private readonly CheckBox _shrink = new() { Content = "Shrink text to fit the zone" };
    private readonly Guid _id;

    public DynamicTextZone Result => new()
    {
        Id = _id, Name = _name.Text?.Trim() ?? "Dynamic text", Template = _template.Text ?? "",
        X = (int)(_x.Value ?? 0), Y = (int)(_y.Value ?? 0), Width = (int)(_width.Value ?? 1),
        Height = (int)(_height.Value ?? 1), FontSize = (float)(_size.Value ?? 48),
        FontFamily = string.IsNullOrWhiteSpace(_font.Text) ? null : _font.Text.Trim(),
        Color = _color.Text?.Trim() ?? "FF000000",
        Alignment = _align.SelectedItem is DynamicTextAlignment a ? a : DynamicTextAlignment.Center,
        VerticalAlignment = _vertical.SelectedItem is DynamicTextVerticalAlignment v ? v : DynamicTextVerticalAlignment.Center,
        Wrap = _wrap.IsChecked == true, ShrinkToFit = _shrink.IsChecked == true
    };

    public DynamicTextZoneDialog(DynamicTextZone zone, bool existing)
    {
        _id = zone.Id;
        Title = existing ? "Edit Dynamic Text Zone" : "Add Dynamic Text Zone";
        Width = 510; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _name.Text = zone.Name; _template.Text = zone.Template;
        _x.Value = zone.X; _y.Value = zone.Y; _width.Value = zone.Width; _height.Value = zone.Height;
        _size.Value = (decimal)zone.FontSize; _font.Text = zone.FontFamily; _color.Text = zone.Color;
        _align.SelectedItem = zone.Alignment; _vertical.SelectedItem = zone.VerticalAlignment;
        _wrap.IsChecked = zone.Wrap; _shrink.IsChecked = zone.ShrinkToFit;

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("145,*"), RowSpacing = 7, ColumnSpacing = 8 };
        string[] labels = { "Zone name", "Text template", "Position (x, y)", "Size (width, height)",
            "Font size", "Font family", "Color", "Horizontal align", "Vertical align", "", "" };
        Control[] controls = { _name, _template, Pair(_x, _y), Pair(_width, _height), _size, _font, _color,
            _align, _vertical, _wrap, _shrink };
        form.RowDefinitions = new RowDefinitions(string.Join(',', Enumerable.Repeat("Auto", controls.Length)));
        for (int i = 0; i < controls.Length; i++)
        {
            var label = new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, i); Grid.SetColumn(controls[i], 1); Grid.SetRow(controls[i], i);
            form.Children.Add(label); form.Children.Add(controls[i]);
        }

        var hint = new TextBlock
        {
            Text = "Use CSV headers in braces, for example: Binder — {StudentName}\nMultiple fields may be used in one zone.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(0);
        var save = new Button { Content = existing ? "Save" : "Add Zone", IsDefault = true };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_template.Text) || !KawaPaint.Engine.ColorBgra.TryParseHexString(_color.Text ?? "", out _)) return;
            Close(1);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right };
        if (existing)
        {
            var delete = new Button { Content = "Delete Zone" };
            delete.Click += (_, _) => Close(2);
            buttons.Children.Add(delete);
        }
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { hint, form, buttons } };
    }

    private static NumericUpDown Number(decimal min, decimal max) => new() { Minimum = min, Maximum = max, Increment = 1 };
    private static StackPanel Pair(params Control[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in controls) { control.Width = 100; panel.Children.Add(control); }
        return panel;
    }
}
