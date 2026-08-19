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
        using var stream = File.Create(path);
        Encode(stream, format, quality);
    }

    /// <summary>Encodes this Surface to a stream (PNG by default).</summary>
    public void Encode(Stream stream, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 100)
    {
        ThrowIfDisposed();
        using var bitmap = WrapSKBitmap();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        data.SaveTo(stream);
    }

    /// <summary>Decodes an image from a stream into a new Surface.</summary>
    public static Surface Decode(Stream stream)
    {
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidOperationException("could not decode image stream");
        var surface = new Surface(codec.Info.Width, codec.Info.Height);
        var info = new SKImageInfo(surface.Width, surface.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var result = codec.GetPixels(info, surface.scan0);
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            surface.Dispose();
            throw new InvalidOperationException($"could not decode image stream ({result})");
        }
        return surface;
    }

    /// <summary>Returns a new Surface scaled to (newWidth,newHeight) with linear sampling.</summary>
    public Surface Resized(int newWidth, int newHeight)
    {
        ThrowIfDisposed();
        var dst = new Surface(newWidth, newHeight);
        using var srcBmp = WrapSKBitmap();
        using var dstBmp = dst.WrapSKBitmap();
        using var canvas = new SKCanvas(dstBmp);
        using var image = SKImage.FromBitmap(srcBmp);
        canvas.DrawImage(image, new SKRect(0, 0, newWidth, newHeight),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return dst;
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

    /// <summary>Bilinear-samples at (x,y); out-of-range coordinates clamp to the nearest edge pixel.</summary>
    public ColorBgra GetBilinearSampleClamped(float x, float y) => BilinearAt(x, y, wrap: false);

    /// <summary>Bilinear-samples at (x,y); out-of-range coordinates wrap around to the opposite edge.</summary>
    public ColorBgra GetBilinearSampleWrapped(float x, float y) => BilinearAt(x, y, wrap: true);

    private ColorBgra BilinearAt(float x, float y, bool wrap)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;

        ColorBgra Sample(int sx, int sy)
        {
            if (wrap)
            {
                sx = ((sx % Width) + Width) % Width;
                sy = ((sy % Height) + Height) % Height;
            }
            else
            {
                sx = Math.Clamp(sx, 0, Width - 1);
                sy = Math.Clamp(sy, 0, Height - 1);
            }
            return *GetPointPointer(sx, sy);
        }

        ColorBgra c00 = Sample(x0, y0), c10 = Sample(x0 + 1, y0);
        ColorBgra c01 = Sample(x0, y0 + 1), c11 = Sample(x0 + 1, y0 + 1);

        static double Lerp(double a, double b, double t) => a + (b - a) * t;
        double b = Lerp(Lerp(c00.B, c10.B, fx), Lerp(c01.B, c11.B, fx), fy);
        double g = Lerp(Lerp(c00.G, c10.G, fx), Lerp(c01.G, c11.G, fx), fy);
        double r = Lerp(Lerp(c00.R, c10.R, fx), Lerp(c01.R, c11.R, fx), fy);
        double a = Lerp(Lerp(c00.A, c10.A, fx), Lerp(c01.A, c11.A, fx), fy);
        return ColorBgra.FromBgra(Clamp.B(b), Clamp.B(g), Clamp.B(r), Clamp.B(a));
    }

    /// <summary>Returns a new Surface containing the (x,y,w,h) region of this one (out-of-bounds = transparent).</summary>
    public Surface Crop(int x, int y, int w, int h)
    {
        ThrowIfDisposed();
        var dst = new Surface(w, h);
        for (int yy = 0; yy < h; yy++)
        {
            int sy = y + yy;
            if ((uint)sy >= (uint)Height) continue;
            ColorBgra* srcRow = (ColorBgra*)GetRowPointer(sy);
            ColorBgra* dstRow = (ColorBgra*)dst.GetRowPointer(yy);
            for (int xx = 0; xx < w; xx++)
            {
                int sx = x + xx;
                if ((uint)sx < (uint)Width) dstRow[xx] = srcRow[sx];
            }
        }
        return dst;
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
        GC.SuppressFinalize(this);   // Surfaces churn (one per stroke); keep them off the finalizer queue
    }

    ~Surface() => Dispose();
}
