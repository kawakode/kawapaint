// KawaPaint - modern port of Paint.NET 3.36.
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
            ThrowIfDisposed();
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException($"({x},{y}) out of bounds of {Width}x{Height}");
            return *GetPointPointer(x, y);
        }
        set
        {
            ThrowIfDisposed();
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException($"({x},{y}) out of bounds of {Width}x{Height}");
            *GetPointPointer(x, y) = value;
        }
    }

    public void Clear(ColorBgra color)
    {
        ThrowIfDisposed();

        // Transparent clears dominate compositing and temporary-surface creation. The buffer is
        // tightly packed, so clear it in one native call instead of assigning one pixel at a time.
        if (color.Bgra == 0)
        {
            NativeMemory.Clear((void*)scan0, checked((nuint)((long)Stride * Height)));
            return;
        }

        for (int y = 0; y < Height; ++y)
        {
            new Span<uint>(GetRowPointer(y), Width).Fill(color.Bgra);
        }
    }

    /// <summary>Clears a clipped rectangular region without touching pixels outside it.</summary>
    public void ClearRect(int x, int y, int width, int height, ColorBgra color)
    {
        ThrowIfDisposed();
        int left = Math.Clamp(x, 0, Width);
        int top = Math.Clamp(y, 0, Height);
        int right = (int)Math.Clamp((long)x + width, 0, Width);
        int bottom = (int)Math.Clamp((long)y + height, 0, Height);
        if (right <= left || bottom <= top) return;

        int count = right - left;
        for (int row = top; row < bottom; row++)
        {
            uint* start = (uint*)GetRowPointer(row) + left;
            if (color.Bgra == 0) NativeMemory.Clear(start, checked((nuint)count * ColorBgra.SizeOf));
            else new Span<uint>(start, count).Fill(color.Bgra);
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

        // Filter in premultiplied space and convert back only at the end. Handed our straight-alpha
        // pixels, Skia filtered (and built mipmaps from) the stored channels directly, so the black
        // held by transparent pixels bled into edges: dark halos on anything resized over
        // transparency. The source is premultiplied by hand for the same reason.
        using var source = Clone();
        source.Premultiply();
        using var srcBmp = new SKBitmap();
        srcBmp.InstallPixels(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul), source.scan0, source.Stride);
        using var image = SKImage.FromBitmap(srcBmp);

        var premulInfo = new SKImageInfo(newWidth, newHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var premul = new SKBitmap())
        {
            premul.InstallPixels(premulInfo, dst.scan0, dst.Stride);
            using var canvas = new SKCanvas(premul);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(image, new SKRect(0, 0, newWidth, newHeight),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        dst.Unpremultiply();
        return dst;
    }

    private void Premultiply()
    {
        for (int y = 0; y < Height; y++)
        {
            ColorBgra* row = (ColorBgra*)GetRowPointer(y);
            for (int x = 0; x < Width; x++)
            {
                ColorBgra c = row[x];
                if (c.A == 255) continue;
                row[x] = ColorBgra.FromBgra(
                    (byte)((c.B * c.A + 127) / 255), (byte)((c.G * c.A + 127) / 255),
                    (byte)((c.R * c.A + 127) / 255), c.A);
            }
        }
    }

    /// <summary>Converts pixels written as premultiplied (by Skia) back to this type's straight alpha.</summary>
    private void Unpremultiply()
    {
        for (int y = 0; y < Height; y++)
        {
            ColorBgra* row = (ColorBgra*)GetRowPointer(y);
            for (int x = 0; x < Width; x++)
            {
                ColorBgra c = row[x];
                if (c.A is 0 or 255) continue;
                int half = c.A / 2;
                row[x] = ColorBgra.FromBgra(
                    (byte)Math.Min(255, (c.B * 255 + half) / c.A),
                    (byte)Math.Min(255, (c.G * 255 + half) / c.A),
                    (byte)Math.Min(255, (c.R * 255 + half) / c.A),
                    c.A);
            }
        }
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

        // 16.16 weights replace eight floating-point lerps. The coordinates arrive as floats, so
        // quantizing their fractional component here loses less than 1/65536 pixel.
        int wx = Math.Clamp((int)(fx * 65536f + 0.5f), 0, 65536);
        int wy = Math.Clamp((int)(fy * 65536f + 0.5f), 0, 65536);
        long w00 = (long)(65536 - wx) * (65536 - wy);
        long w10 = (long)wx * (65536 - wy);
        long w01 = (long)(65536 - wx) * wy;
        long w11 = (long)wx * wy;

        // Color is weighted by alpha as well as distance: a transparent neighbor contributes
        // coverage but no color, so a shape's edge fades out instead of darkening toward the
        // (meaningless) black that transparent pixels store.
        long a00 = c00.A * w00, a10 = c10.A * w10, a01 = c01.A * w01, a11 = c11.A * w11;
        long alpha = a00 + a10 + a01 + a11;
        if (alpha == 0) return ColorBgra.Transparent;
        long half = alpha >> 1;

        byte Mix(byte v00, byte v10, byte v01, byte v11)
            => (byte)((v00 * a00 + v10 * a10 + v01 * a01 + v11 * a11 + half) / alpha);

        return ColorBgra.FromBgra(
            Mix(c00.B, c10.B, c01.B, c11.B),
            Mix(c00.G, c10.G, c01.G, c11.G),
            Mix(c00.R, c10.R, c01.R, c11.R),
            (byte)((alpha + (1L << 31)) >> 32));
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

    /// <summary>Copies a clipped rectangle from the same coordinates in another same-sized
    /// surface. Used by live previews to erase only their previous footprint.</summary>
    public void CopyRectFrom(Surface other, int x, int y, int width, int height)
    {
        ThrowIfDisposed();
        if (other.Width != Width || other.Height != Height)
            throw new ArgumentException("surface size mismatch");

        int left = Math.Clamp(x, 0, Width);
        int top = Math.Clamp(y, 0, Height);
        int right = Math.Clamp(checked(x + width), 0, Width);
        int bottom = Math.Clamp(checked(y + height), 0, Height);
        if (right <= left || bottom <= top) return;

        nuint rowBytes = checked((nuint)(right - left) * ColorBgra.SizeOf);
        for (int row = top; row < bottom; row++)
        {
            void* source = other.GetRowPointer(row) + (long)left * ColorBgra.SizeOf;
            void* destination = GetRowPointer(row) + (long)left * ColorBgra.SizeOf;
            NativeMemory.Copy(source, destination, rowBytes);
        }
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
