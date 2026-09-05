using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KawaPaint.Engine;
using SkiaSharp;

namespace KawaPaint.App;

public partial class MainView
{
    private TextBox? _textEditor;
    private Border? _textFrame;
    private Border? _textActions;
    private Layer? _textLayer;
    private Document? _textDocument;
    private Point _textPosition;
    private int _textSize = 48;
    private ColorBgra _textColor;
    private string _textFontFamily = "";
    private IPointer? _textDragPointer;
    private Point _textDragStart;
    private Point _textDragPosition;

    private void InitializeTextEditing()
    {
        Canvas.ViewChanged += PositionTextEditor;
        TextEditingLayer.SizeChanged += (_, _) => PositionTextEditor();
        Canvas.DocumentChanged += (_, _) =>
        {
            if (_textEditor is not null &&
                (!ReferenceEquals(_textDocument, Canvas.Document) || !ReferenceEquals(_textLayer, Canvas.ActiveLayer)))
                FinishTextEditing(commit: false);
        };
        // Commit before an outside click changes tools, layers, selection, or opens a menu.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_textEditor is null || e.Source is not Visual source ||
                source == TextEditingLayer || TextEditingLayer.IsVisualAncestorOf(source)) return;
            FinishTextEditing();
        }, RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => FinishTextEditing(commit: false);
    }

    private void OnTextRequested(int x, int y)
    {
        if (Canvas.ActiveLayer is null || _textPromptOpen) return;
        if (IsPlayingDemo)
        {
            StatusText.Text = "Demo: skipped Text (the typed string isn't recorded)";
            return;
        }
        FinishTextEditing();
        RecordSkipped("Text");
        _textLayer = Canvas.ActiveLayer;
        _textDocument = Canvas.Document;
        _textPosition = new Point(x, y);
        _textColor = Canvas.BrushColor;
        _textFontFamily = SKTypeface.Default.FamilyName;
        var editor = new TextBox
        {
            Name = "InlineTextInput",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily(_textFontFamily),
            Foreground = new SolidColorBrush(Color.FromArgb(_textColor.A, _textColor.R, _textColor.G, _textColor.B)),
            Background = Brushes.Transparent,
            CaretBrush = Brushes.DodgerBlue,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 24,
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        // Fluent supplies opaque backgrounds in its focus/hover states by default.
        foreach (string key in new[] { "TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver" })
            editor.Resources[key] = Brushes.Transparent;
        _textEditor = editor;
        _textFrame = new Border
        {
            BorderBrush = Brushes.DodgerBlue,
            BorderThickness = new Thickness(1),
            Child = editor
        };
        var move = new Border
        {
            Name = "TextMoveHandle", Child = Icons.Create("Move"),
            Width = 30, Height = 30, Padding = new Thickness(5),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        Avalonia.Automation.AutomationProperties.SetName(move, "Move text");
        ToolTip.SetTip(move, "Drag to move text");
        move.PointerPressed += (_, e) =>
        {
            if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(move).Properties.IsLeftButtonPressed) return;
            _textDragStart = e.GetPosition(Canvas);
            _textDragPosition = _textPosition;
            _textDragPointer = e.Pointer;
            e.Pointer.Capture(move);
            e.Handled = true;
        };
        move.PointerMoved += (_, e) =>
        {
            if (_textDragPointer != e.Pointer) return;
            Vector delta = (e.GetPosition(Canvas) - _textDragStart) / Canvas.Zoom;
            _textPosition = new Point(Math.Round(_textDragPosition.X + delta.X), Math.Round(_textDragPosition.Y + delta.Y));
            PositionTextEditor();
            e.Handled = true;
        };
        move.PointerReleased += (_, e) =>
        {
            if (_textDragPointer != e.Pointer) return;
            _textDragPointer = null;
            e.Pointer.Capture(null);
            editor.Focus();
            e.Handled = true;
        };
        move.PointerCaptureLost += (_, _) => _textDragPointer = null;
        var size = new NumericUpDown
        {
            Name = "InlineTextSize", Minimum = 8, Maximum = 240, Increment = 1,
            Value = _textSize, Width = 54, Height = 30, MinHeight = 0, ShowButtonSpinner = false,
            Padding = new Thickness(4, 0)
        };
        Avalonia.Automation.AutomationProperties.SetName(size, "Text size");
        ToolTip.SetTip(size, "Text size in pixels");
        size.ValueChanged += (_, _) =>
        {
            _textSize = (int)(size.Value ?? 48);
            PositionTextEditor();
        };
        var finalize = new Button
        {
            Name = "FinalizeTextButton", Content = "Finalize", Height = 30, MinHeight = 0,
            Padding = new Thickness(8, 2), FontSize = 12
        };
        ToolTip.SetTip(finalize, "Finalize text (Ctrl+Enter)");
        finalize.Click += (_, _) => FinishTextEditing();
        _textActions = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
            BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(2),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                Children = { move, size, finalize }
            }
        };
        TextEditingLayer.Children.Add(_textFrame);
        TextEditingLayer.Children.Add(_textActions);
        editor.TextChanged += (_, _) => PositionTextEditor();
        TextEditingLayer.AddHandler(KeyDownEvent, OnTextEditorKeyDown, RoutingStrategies.Tunnel);
        PositionTextEditor();
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_textEditor, editor)) editor.Focus();
        }, DispatcherPriority.Input);
        StatusText.Text = "Type text · drag the handle to move · Ctrl+Enter to finalize · Esc to cancel";
    }

    private void PositionTextEditor()
    {
        if (_textEditor is not { } editor || _textFrame is null || _textActions is null) return;
        double zoom = Canvas.Zoom;
        using var typeface = SKTypeface.FromFamilyName(_textFontFamily);
        using var font = new SKFont(typeface, _textSize);
        var metrics = font.Metrics;
        double lineHeight = metrics.Descent - metrics.Ascent + metrics.Leading;
        double width = 24 / zoom;
        string[] lines = (editor.Text ?? "").Replace("\r", "").Split('\n');
        foreach (string line in lines) width = Math.Max(width, font.MeasureText(line));
        editor.FontSize = _textSize * zoom;
        editor.LineHeight = lineHeight * zoom;
        editor.Width = width * zoom + 4;
        editor.Height = lineHeight * lines.Length * zoom + 2;
        double left = Canvas.Origin.X + _textPosition.X * zoom;
        double top = Canvas.Origin.Y + _textPosition.Y * zoom;
        Avalonia.Controls.Canvas.SetLeft(_textFrame, left - 1);
        Avalonia.Controls.Canvas.SetTop(_textFrame, top - 1);
        _textActions.Measure(Size.Infinity);
        Size actions = _textActions.DesiredSize;
        Avalonia.Controls.Canvas.SetLeft(_textActions, Math.Clamp(left, 0, Math.Max(0, TextEditingLayer.Bounds.Width - actions.Width)));
        double actionsTop = top - actions.Height - 5;
        if (actionsTop < 0) actionsTop = top + editor.Height + 5;
        Avalonia.Controls.Canvas.SetTop(_textActions, Math.Clamp(actionsTop, 0, Math.Max(0, TextEditingLayer.Bounds.Height - actions.Height)));
    }

    private void OnTextEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            FinishTextEditing(commit: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            FinishTextEditing();
            e.Handled = true;
        }
    }

    private void FinishTextEditing(bool commit = true)
    {
        if (_textEditor is not { } editor) return;
        string text = editor.Text ?? "";
        var layer = _textLayer;
        bool validTarget = ReferenceEquals(_textDocument, Canvas.Document) && ReferenceEquals(layer, Canvas.ActiveLayer);
        _textEditor = null;
        _textFrame = null;
        _textActions = null;
        _textLayer = null;
        _textDocument = null;
        var pointer = _textDragPointer;
        _textDragPointer = null;
        pointer?.Capture(null);
        TextEditingLayer.RemoveHandler(KeyDownEvent, OnTextEditorKeyDown);
        TextEditingLayer.Children.Clear();
        if (commit && validTarget && layer is not null && !string.IsNullOrEmpty(text))
        {
            var snapshot = layer.Surface.Clone();
            int x = (int)_textPosition.X, y = (int)_textPosition.Y;
            TextOps.DrawText(layer.Surface, text, x, y, _textSize, _textColor, _textFontFamily);
            if (Canvas.Selection is { IsActive: true }) Canvas.Selection.Clip(layer.Surface, snapshot);
            Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, "Text"));
            Canvas.RenderComposite();
            Canvas.InvalidateVisual();
            Canvas.NotifyLayersChanged();
            _scriptRecorder.NoteAction("text.draw", new double[] { x, y, _textSize, _textColor.Bgra }, new[] { text });
            StatusText.Text = "Added text";
        }
        else StatusText.Text = "Text cancelled";
        Canvas.Focus();
    }
}
