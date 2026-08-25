using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KawaPaint.Engine;
using KawaPaint.Engine.Codecs;

namespace KawaPaint.App;

public partial class MainView
{
    private bool _suppressFrames;
    private DispatcherTimer? _animationTimer;
    private readonly Dictionary<DocumentFrame, (WriteableBitmap Bitmap, int Version)> _frameThumbnails = new();
    private const int FrameThumbWidth = 96;
    private const int FrameThumbHeight = 60;

    private void RebuildTimeline()
    {
        if (FrameList is null || Canvas.Document is not { } document) return;
        _suppressFrames = true;
        try
        {
            FrameList.Items.Clear();
            for (int index = 0; index < document.FrameCount; index++)
            {
                DocumentFrame frame = document.Frames[index];
                var content = new StackPanel
                {
                    Width = ShowFramePreviewsCheck.IsChecked == true ? 104 : 88,
                    Margin = new Thickness(3)
                };
                if (ShowFramePreviewsCheck.IsChecked == true)
                {
                    content.Children.Add(new Border
                    {
                        Width = FrameThumbWidth,
                        Height = FrameThumbHeight,
                        Background = Brushes.DimGray,
                        BorderBrush = Brushes.Gray,
                        BorderThickness = new Thickness(1),
                        Child = new Image { Source = FrameThumbnailFor(document, frame), Stretch = Stretch.Uniform }
                    });
                }
                content.Children.Add(new TextBlock
                {
                    Text = frame.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                content.Children.Add(new TextBlock
                {
                    Text = $"{frame.DurationMs} ms",
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                var item = new ListBoxItem { Tag = index, Content = content };
                FrameList.Items.Add(item);
                if (index == document.ActiveFrameIndex) FrameList.SelectedItem = item;
            }
            FrameDurationBox.Value = document.ActiveFrame.DurationMs;
            PruneFrameThumbnails(document);
        }
        finally { _suppressFrames = false; }
    }

    private void OnFramePreviewsChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressFrames) return;
        bool show = ShowFramePreviewsCheck.IsChecked == true;
        _settings.Update(settings => settings.Workspace.ShowFramePreviews = show);
        RebuildTimeline();
        StatusText.Text = show ? "Timeline previews enabled" : "Timeline previews hidden";
    }

    private WriteableBitmap FrameThumbnailFor(Document document, DocumentFrame frame)
    {
        int version = Canvas.FrameContentVersion(frame);
        if (_frameThumbnails.TryGetValue(frame, out var cached))
        {
            if (cached.Version == version) return cached.Bitmap;
            RenderFrameThumbnail(document, frame, cached.Bitmap);
            _frameThumbnails[frame] = (cached.Bitmap, version);
            return cached.Bitmap;
        }

        var bitmap = new WriteableBitmap(new PixelSize(FrameThumbWidth, FrameThumbHeight),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        RenderFrameThumbnail(document, frame, bitmap);
        _frameThumbnails[frame] = (bitmap, version);
        return bitmap;
    }

    private static unsafe void RenderFrameThumbnail(Document document, DocumentFrame frame, WriteableBitmap target)
    {
        using Surface composite = AnimatedGifEncoder.RenderDocumentFrame(document, frame);
        double scale = Math.Min((double)FrameThumbWidth / composite.Width,
            (double)FrameThumbHeight / composite.Height);
        int width = Math.Max(1, (int)Math.Round(composite.Width * scale));
        int height = Math.Max(1, (int)Math.Round(composite.Height * scale));
        using Surface small = composite.Resized(width, height);
        using ILockedFramebuffer framebuffer = target.Lock();

        new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * FrameThumbHeight).Clear();
        int left = (FrameThumbWidth - width) / 2;
        int top = (FrameThumbHeight - height) / 2;
        int rowBytes = width * ColorBgra.SizeOf;
        byte* destination = (byte*)framebuffer.Address + (long)top * framebuffer.RowBytes + left * ColorBgra.SizeOf;
        for (int y = 0; y < height; y++)
            Buffer.MemoryCopy(small.GetRowPointer(y), destination + (long)y * framebuffer.RowBytes,
                framebuffer.RowBytes - left * ColorBgra.SizeOf, rowBytes);
    }

    private void PruneFrameThumbnails(Document document)
    {
        List<DocumentFrame>? removed = null;
        foreach (DocumentFrame frame in _frameThumbnails.Keys)
            if (!document.Frames.Contains(frame)) (removed ??= new()).Add(frame);
        if (removed is null) return;
        foreach (DocumentFrame frame in removed)
        {
            DisposeLater(_frameThumbnails[frame].Bitmap);
            _frameThumbnails.Remove(frame);
        }
    }

    private void DisposeTimelineResources()
    {
        _animationTimer?.Stop();
        foreach (var cached in _frameThumbnails.Values) cached.Bitmap.Dispose();
        _frameThumbnails.Clear();
    }

    private void OnFrameSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressFrames || FrameList.SelectedItem is not ListBoxItem { Tag: int index }) return;
        Canvas.SetActiveFrame(index);
        StatusText.Text = $"Frame {index + 1} of {Canvas.Document!.FrameCount}";
    }

    private void OnPreviousFrame(object? sender, RoutedEventArgs e) => MoveFrame(-1);
    private void OnNextFrame(object? sender, RoutedEventArgs e) => MoveFrame(1);

    private void OnPlayAnimation(object? sender, RoutedEventArgs e)
    {
        if (_animationTimer?.IsEnabled == true)
        {
            _animationTimer.Stop();
            PlayAnimationButton.Content = "Play";
            return;
        }
        if (Canvas.Document is not { FrameCount: > 1 } document)
        {
            StatusText.Text = "Add another frame before playing";
            return;
        }
        _animationTimer ??= new DispatcherTimer();
        _animationTimer.Tick -= OnAnimationTick;
        _animationTimer.Tick += OnAnimationTick;
        _animationTimer.Interval = TimeSpan.FromMilliseconds(document.ActiveFrame.DurationMs);
        _animationTimer.Start();
        PlayAnimationButton.Content = "Stop";
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (Canvas.Document is not { FrameCount: > 1 } document)
        {
            _animationTimer?.Stop();
            PlayAnimationButton.Content = "Play";
            return;
        }
        MoveFrame(1);
        _animationTimer!.Interval = TimeSpan.FromMilliseconds(document.ActiveFrame.DurationMs);
    }

    private void MoveFrame(int delta)
    {
        if (Canvas.Document is not { } document) return;
        int index = (document.ActiveFrameIndex + delta + document.FrameCount) % document.FrameCount;
        Canvas.SetActiveFrame(index);
        StatusText.Text = $"Frame {index + 1} of {document.FrameCount}";
    }

    private void OnAddFrame(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Document is not { } document) return;
        document.AddFrame(durationMs: document.ActiveFrame.DurationMs);
        Canvas.SetActiveFrame(document.ActiveFrameIndex);
        MarkDirty();
        StatusText.Text = $"Added frame {document.ActiveFrameIndex + 1}";
    }

    private void OnDuplicateFrame(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Document is not { } document) return;
        document.AddFrame(durationMs: document.ActiveFrame.DurationMs, cloneCurrent: true);
        Canvas.SetActiveFrame(document.ActiveFrameIndex);
        MarkDirty();
        StatusText.Text = $"Duplicated as frame {document.ActiveFrameIndex + 1}";
    }

    private void OnDeleteFrame(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Document is not { } document) return;
        if (document.FrameCount == 1)
        {
            StatusText.Text = "An animation must keep at least one frame";
            return;
        }
        int removed = document.ActiveFrameIndex;
        document.RemoveFrameAt(removed);
        Canvas.SetActiveFrame(document.ActiveFrameIndex);
        MarkDirty();
        StatusText.Text = $"Deleted frame {removed + 1}";
    }

    private void OnFrameDurationChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_suppressFrames || Canvas.Document is not { } document || FrameDurationBox.Value is not { } value) return;
        int duration = Math.Clamp((int)value, 10, 600_000);
        if (document.ActiveFrame.DurationMs == duration) return;
        document.ActiveFrame.DurationMs = duration;
        MarkDirty();
        RebuildTimeline();
    }
}
