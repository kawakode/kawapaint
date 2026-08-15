using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using KawaPaint.Engine;

namespace KawaPaint.App;

public partial class MainWindow : Window
{
    private Surface? _surface;

    public MainWindow()
    {
        InitializeComponent();
        LoadDemoSurface();
    }

    private void SetSurface(Surface surface, string status)
    {
        _surface?.Dispose();
        _surface = surface;
        Canvas.SetSurface(surface);
        StatusText.Text = status;
    }

    private unsafe void LoadDemoSurface()
    {
        int w = 800, h = 600;
        var surface = new Surface(w, h);
        for (int y = 0; y < h; y++)
        {
            ColorBgra* row = (ColorBgra*)surface.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                row[x] = ColorBgra.FromBgra((byte)(x * 255 / w), (byte)(y * 255 / h), 80, 255);
            }
        }

        var red = ColorBgra.FromBgra(0, 0, 220, 128);
        for (int y = 120; y < 400; y++)
            for (int x = 160; x < 520; x++)
                surface[x, y] = ColorBgra.BlendOver(surface[x, y], red);

        SetSurface(surface, $"Demo surface {w}×{h} — wheel to zoom, middle/right-drag to pan");
    }

    private async void OnOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" }
                }
            }
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        try
        {
            string path = file.Path.LocalPath;
            var surface = Surface.Load(path);
            SetSurface(surface, $"{System.IO.Path.GetFileName(path)} — {surface.Width}×{surface.Height}");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Open failed: " + ex.Message;
        }
    }

    private async void OnSaveAs(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_surface is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save image as PNG",
            DefaultExtension = "png",
            SuggestedFileName = "untitled.png"
        });

        if (file is null) return;

        try
        {
            _surface.Save(file.Path.LocalPath);
            StatusText.Text = "Saved " + System.IO.Path.GetFileName(file.Path.LocalPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save failed: " + ex.Message;
        }
    }

    private void OnNew(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var surface = new Surface(800, 600);
        surface.Clear(ColorBgra.White);
        SetSurface(surface, "New 800×600 canvas — left-drag to draw");
    }

    private void OnColor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hex) return;

        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte bl = Convert.ToByte(hex.Substring(5, 2), 16);

        Canvas.BrushColor = ColorBgra.FromBgr(bl, g, r);
        ColorSwatch.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(r, g, bl));
    }

    private void OnSize(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int size = (int)Math.Round(e.NewValue);
        if (Canvas is not null) Canvas.BrushWidth = size;
        if (SizeLabel is not null) SizeLabel.Text = size + " px";
    }

    private void OnExit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
