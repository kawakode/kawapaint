// KawaPaint — modern port of Paint.NET 3.36.
// Surface: a 32-bit BGRA, top-down, tightly-packed (stride = width*4) pixel buffer held in
// unmanaged memory. This is the engine's fundamental image container. The layout matches
// SkiaSharp's Bgra8888 exactly, so import/export and on-screen display are zero-copy.

using System.Runtime.InteropServices;
using SkiaSharp;

namespace KawaPaint.Engine;

public sealed unsafe class Surface : IDisposable
{
    private IntPtr scan0;
    private bool disposed;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    /// <summary>Pointer to the first (0,0) pixel. Valid until the Surface is disposed.</summary>
    public IntPtr Scan0 => scan0;

    public Surface(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        Width = width;
        Height = height;
        Stride = checked(width * ColorBgra.SizeOf);

        long bytes = (long)Stride * height;
        scan0 = Marshal.AllocHGlobal(new IntPtr(bytes));
        GC.AddMemoryPressure(bytes);
        NativeMemory.Clear((void*)scan0, (nuint)bytes);
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(Surface));
    }

    public byte* GetRowPointer(int y)
    {
        return (byte*)scan0 + (long)y * Stride;
    }

    public ColorBgra* GetPointPointer(int x, int y)
    {
        return (ColorBgra*)(GetRowPointer(y) + (long)x * ColorBgra.SizeOf);
    }

    public ColorBgra this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException($"({x},{y}) out of bounds of {Width}x{Height}");
            return *GetPointPointer(x, y);
        }
        set
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException($"({x},{y}) out of bounds of {Width}x{Height}");
            *GetPointPointer(x, y) = value;
        }
    }

    public void Clear(ColorBgra color)
    {
        ThrowIfDisposed();
        for (int y = 0; y < Height; ++y)
        {
            ColorBgra* row = (ColorBgra*)GetRowPointer(y);
            for (int x = 0; x < Width; ++x)
            {
                row[x] = color;
            }
        }
    }

    // ---- SkiaSharp interop -------------------------------------------------

    /// <summary>
    /// Wraps this Surface's memory in an SKBitmap without copying. The returned bitmap
    /// shares the Surface's buffer, so it must not outlive the Surface.
    /// </summary>
    public SKBitmap WrapSKBitmap()
    {
        ThrowIfDisposed();
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap();
        bitmap.InstallPixels(info, scan0, Stride);
        return bitmap;
    }

    /// <summary>Copies pixels from an SKBitmap (any format) into this Surface, which must match its size.</summary>
    public void CopyFromSKBitmap(SKBitmap source)
    {
        ThrowIfDisposed();
        if (source.Width != Width || source.Height != Height)
            throw new ArgumentException("bitmap size does not match surface size");

        using var dst = WrapSKBitmap();
        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(source, 0, 0, paint);
    }

    public static Surface Load(string path)
    {
        using var codec = SKCodec.Create(path)
            ?? throw new InvalidOperationException($"could not decode image: {path}");
        var surface = new Surface(codec.Info.Width, codec.Info.Height);
        var info = new SKImageInfo(surface.Width, surface.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var result = codec.GetPixels(info, surface.scan0);
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            surface.Dispose();
            throw new InvalidOperationException($"could not decode image ({result}): {path}");
        }
        return surface;
    }

    public void Save(string path, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 100)
    {
        ThrowIfDisposed();
        using var bitmap = WrapSKBitmap();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
    }

    /// <summary>Returns a new Surface with a copy of this one's pixels.</summary>
    public Surface Clone()
    {
        ThrowIfDisposed();
        var copy = new Surface(Width, Height);
        long bytes = (long)Stride * Height;
        NativeMemory.Copy((void*)scan0, (void*)copy.scan0, (nuint)bytes);
        return copy;
    }

    /// <summary>Copies all pixels from another same-sized Surface into this one.</summary>
    public void CopyFrom(Surface other)
    {
        ThrowIfDisposed();
        if (other.Width != Width || other.Height != Height)
            throw new ArgumentException("surface size mismatch");
        long bytes = (long)Stride * Height;
        NativeMemory.Copy((void*)other.scan0, (void*)scan0, (nuint)bytes);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (scan0 != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(scan0);
            GC.RemoveMemoryPressure((long)Stride * Height);
            scan0 = IntPtr.Zero;
        }
    }

    ~Surface() => Dispose();
}
