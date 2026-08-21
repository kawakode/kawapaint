// KawaPaint - bridges KawaPaint.Engine.Surface to a real PaintDotNet.Surface/RenderArgs pair.
// Both are already byte-identical BGRA32 (non-premultiplied, top-down, tight width*4 stride) per
// both codebases' own documentation, so no pixel conversion is ever needed - just a raw memory
// copy. A real MemoryBlock (PaintDotNet.Surface's backing store) has no public constructor that
// wraps an externally-owned pointer (confirmed by reflecting its full member list before writing
// this), so zero-copy isn't available; this ships copy-based as the only path. The copy itself is
// a single Buffer.MemoryCopy via MemoryBlock.Pointer, not a per-pixel loop, so it's still cheap.

using System;

namespace KawaPaint.Engine.Plugins.Pdn;

/// <summary>
/// Owns one real PaintDotNet.Surface plus the RenderArgs aliasing it, so a caller can free both
/// with a single <c>using</c>. Both really are IDisposable on the paint.net side - the Surface owns
/// a native MemoryBlock, and RenderArgs lazily builds a GDI+ Bitmap and Graphics over that memory -
/// and PdnClassicEffectAdapter.Apply builds two of these per call, on every debounced preview tick.
/// Leaking them leaked full-canvas unmanaged buffers and GDI handles per tick, not per commit.
///
/// Dispose order is load-bearing: RenderArgs first, because its Bitmap/Graphics alias the Surface's
/// memory. The Surface is disposed separately and unconditionally because RenderArgs explicitly does
/// NOT take ownership of it - paint.net's own RenderArgs docs say so outright ("This instance of
/// RenderArgs does not take ownership of this Surface", and its Dispose only frees the Bitmap and
/// Graphics), so this is not a double free.
/// </summary>
internal sealed class PdnRenderTarget : IDisposable
{
    public PdnRenderTarget(object pdnSurface, object renderArgs)
    {
        PdnSurface = pdnSurface;
        RenderArgs = renderArgs;
    }

    public object PdnSurface { get; }
    public object RenderArgs { get; }

    public void Dispose()
    {
        // `as IDisposable` rather than a hard cast: these are reflection-obtained instances of types
        // this project has no compile-time reference to, so degrade to "don't free it" rather than
        // throwing if a future paint.net ever stops implementing IDisposable on either.
        (RenderArgs as IDisposable)?.Dispose();
        (PdnSurface as IDisposable)?.Dispose();
    }
}

internal static class PdnSurfaceBridge
{
    /// <summary>Builds a real PaintDotNet.Surface the same size as <paramref name="kawaSurface"/>,
    /// copies its pixels in, and wraps it in a real RenderArgs. The caller owns the result and must
    /// dispose it - see PdnRenderTarget for why that matters more than it looks.</summary>
    public static unsafe PdnRenderTarget Wrap(Surface kawaSurface, PdnReflectionSchema schema)
    {
        object pdnSurface = schema.SurfaceConstructor.Invoke(new object[] { kawaSurface.Width, kawaSurface.Height })!;

        try
        {
            int pdnStride = (int)schema.SurfaceStride.GetValue(pdnSurface)!;
            if (pdnStride != kawaSurface.Stride)
                throw new InvalidOperationException(
                    $"PDN bridge unavailable: real PaintDotNet.Surface stride ({pdnStride}) does not match KawaPaint.Engine.Surface stride ({kawaSurface.Stride}) - pixel layouts have diverged.");

            CopyInto(kawaSurface, pdnSurface, schema);

            object renderArgs = schema.RenderArgsConstructor.Invoke(new object[] { pdnSurface })!;
            return new PdnRenderTarget(pdnSurface, renderArgs);
        }
        catch
        {
            // The stride check and the RenderArgs construction both sit between allocating the
            // native surface and handing ownership to the caller; without this, throwing there
            // would leak exactly what this type exists to stop leaking.
            (pdnSurface as IDisposable)?.Dispose();
            throw;
        }
    }

    public static unsafe void CopyInto(Surface kawaSurface, object pdnSurface, PdnReflectionSchema schema)
    {
        IntPtr dst = GetScan0Pointer(pdnSurface, schema);
        long bytes = (long)kawaSurface.Stride * kawaSurface.Height;
        Buffer.MemoryCopy((void*)kawaSurface.Scan0, (void*)dst, bytes, bytes);
    }

    public static unsafe void CopyBack(Surface kawaSurface, object pdnSurface, PdnReflectionSchema schema)
    {
        IntPtr src = GetScan0Pointer(pdnSurface, schema);
        long bytes = (long)kawaSurface.Stride * kawaSurface.Height;
        Buffer.MemoryCopy((void*)src, (void*)kawaSurface.Scan0, bytes, bytes);
    }

    private static IntPtr GetScan0Pointer(object pdnSurface, PdnReflectionSchema schema)
    {
        object memoryBlock = schema.SurfaceScan0.GetValue(pdnSurface)!;
        return (IntPtr)schema.MemoryBlockPointer.GetValue(memoryBlock)!;
    }
}
