// KawaPaint — bridges KawaPaint.Engine.Surface to a real PaintDotNet.Surface/RenderArgs pair.
// Both are already byte-identical BGRA32 (non-premultiplied, top-down, tight width*4 stride) per
// both codebases' own documentation, so no pixel conversion is ever needed — just a raw memory
// copy. A real MemoryBlock (PaintDotNet.Surface's backing store) has no public constructor that
// wraps an externally-owned pointer (confirmed by reflecting its full member list before writing
// this), so zero-copy isn't available; this ships copy-based as the only path. The copy itself is
// a single Buffer.MemoryCopy via MemoryBlock.Pointer, not a per-pixel loop, so it's still cheap.

using System;

namespace KawaPaint.Engine.Plugins.Pdn;

internal static class PdnSurfaceBridge
{
    /// <summary>Builds a real PaintDotNet.Surface the same size as <paramref name="kawaSurface"/>,
    /// copies its pixels in, and wraps it in a real RenderArgs.</summary>
    public static unsafe (object PdnSurface, object RenderArgs) Wrap(Surface kawaSurface, PdnReflectionSchema schema)
    {
        object pdnSurface = schema.SurfaceConstructor.Invoke(new object[] { kawaSurface.Width, kawaSurface.Height })!;

        int pdnStride = (int)schema.SurfaceStride.GetValue(pdnSurface)!;
        if (pdnStride != kawaSurface.Stride)
            throw new InvalidOperationException(
                $"PDN bridge unavailable: real PaintDotNet.Surface stride ({pdnStride}) does not match KawaPaint.Engine.Surface stride ({kawaSurface.Stride}) — pixel layouts have diverged.");

        CopyInto(kawaSurface, pdnSurface, schema);

        object renderArgs = schema.RenderArgsConstructor.Invoke(new object[] { pdnSurface })!;
        return (pdnSurface, renderArgs);
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
