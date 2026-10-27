// KawaPaint - rough memory cost of an image size, so dialogs can warn before a size that would
// exhaust RAM. A 20000x20000 layer alone is 1.6 GB, and the editor keeps more than the layers.

namespace KawaPaint.Engine;

public enum ImageMemoryFit { Comfortable, Tight, TooLarge }

public static class ImageMemory
{
    /// <summary>Surfaces kept besides the layers: the composite, the display copy and one working
    /// surface for the operation in progress.</summary>
    private const int ExtraSurfaces = 3;

    /// <summary>Bytes needed to hold a document of this size with <paramref name="layers"/> layers.
    /// <paramref name="previousBytes"/> covers an operation (resize, canvas size) that builds the
    /// new layers while the old ones are still alive.</summary>
    public static long Estimate(int width, int height, int layers, long previousBytes = 0) =>
        (long)width * height * 4 * (Math.Max(1, layers) + ExtraSurfaces) + previousBytes;

    /// <summary>Physical memory the process may use (the machine's RAM or its container limit).</summary>
    public static long Budget => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>Tight above half of <paramref name="budget"/>, too large above 90 %.</summary>
    public static ImageMemoryFit Check(long bytes, long budget)
    {
        if (budget <= 0) return ImageMemoryFit.Comfortable;
        if (bytes > budget / 10 * 9) return ImageMemoryFit.TooLarge;
        return bytes > budget / 2 ? ImageMemoryFit.Tight : ImageMemoryFit.Comfortable;
    }

    public static string Format(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.0} GB"
        : $"{Math.Max(1, bytes >> 20)} MB";
}
