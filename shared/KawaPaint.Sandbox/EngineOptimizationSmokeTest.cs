using KawaPaint.Engine;

namespace KawaPaint.Sandbox;

internal static class EngineOptimizationSmokeTest
{
    public static void RunAll()
    {
        ClearPreservesExactPixels();
        ShiftIntoClipsAndCopiesRows();
        FloodFillAndMagicWandKeepTheirSemantics();
        NormalBlendFastPathIsPixelExact();
        BlendSpansAndDirtyRectsStayExact();
        IncrementalPolygonsAndCoverageStayExact();
        HistoryResidentBytesStayExact();
        SelectionBoundsAndClipStayExact();
        TiledRotationStaysExact();
        RectangularCopyIsClippedAndExact();
        LutEffectsStayExact();
        RadialBlurPrecomputationStaysExact();
        Console.WriteLine("ENGINE OPTIMIZATION SMOKE OK");
    }

    private static void ClearPreservesExactPixels()
    {
        using var surface = new Surface(7, 5);
        var opaque = ColorBgra.FromBgra(17, 34, 51, 68);
        surface.Clear(opaque);
        AssertEveryPixel(surface, opaque, "non-transparent clear");

        surface.Clear(ColorBgra.Transparent);
        AssertEveryPixel(surface, ColorBgra.Transparent, "transparent clear");
    }

    private static void ShiftIntoClipsAndCopiesRows()
    {
        using var source = new Surface(4, 3);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                source[x, y] = ColorBgra.FromUInt32((uint)(1 + y * source.Width + x));

        foreach (var (dx, dy) in new[] { (-1, 1), (2, -1), (0, 0), (9, 9), (-9, -9) })
        {
            using var destination = new Surface(5, 4);
            destination.Clear(ColorBgra.White);
            SurfaceOps.ShiftInto(destination, source, dx, dy);

            for (int y = 0; y < destination.Height; y++)
            for (int x = 0; x < destination.Width; x++)
            {
                int sx = x - dx, sy = y - dy;
                ColorBgra expected = (uint)sx < (uint)source.Width && (uint)sy < (uint)source.Height
                    ? source[sx, sy]
                    : ColorBgra.Transparent;
                Assert(destination[x, y] == expected,
                    $"shift ({dx},{dy}) mismatch at ({x},{y}): {destination[x, y]} != {expected}");
            }
        }
    }

    private static void FloodFillAndMagicWandKeepTheirSemantics()
    {
        var region = ColorBgra.FromBgra(10, 20, 30, 255);
        var barrier = ColorBgra.FromBgra(200, 190, 180, 255);
        var replacement = ColorBgra.FromBgra(1, 2, 3, 255);
        using var surface = new Surface(6, 5);
        surface.Clear(region);
        for (int y = 0; y < surface.Height; y++) surface[3, y] = barrier;
        surface[3, 2] = region; // one-pixel bridge joins both halves

        using var fillSurface = surface.Clone();
        FloodFill.Fill(fillSurface, 0, 0, replacement);
        for (int y = 0; y < fillSurface.Height; y++)
        for (int x = 0; x < fillSurface.Width; x++)
        {
            ColorBgra expected = x == 3 && y != 2 ? barrier : replacement;
            Assert(fillSurface[x, y] == expected, $"flood-fill mismatch at ({x},{y})");
        }

        var selection = new Selection(surface.Width, surface.Height);
        FloodFill.Select(surface, 0, 0, selection);
        for (int y = 0; y < surface.Height; y++)
        for (int x = 0; x < surface.Width; x++)
        {
            bool expected = x != 3 || y == 2;
            Assert(selection.IsSelected(x, y) == expected, $"magic-wand mismatch at ({x},{y})");
        }
    }

    private static void NormalBlendFastPathIsPixelExact()
    {
        var random = new Random(1731);
        for (int i = 0; i < 100_000; i++)
        {
            var dst = ColorBgra.FromUInt32((uint)random.NextInt64(0, 1L << 32));
            var src = ColorBgra.FromUInt32((uint)random.NextInt64(0, 1L << 32));
            int opacity = random.Next(256);
            ColorBgra expected = CompositeNormalReference(dst, src, opacity);
            ColorBgra actual = Blending.Composite(BlendMode.Normal, dst, src, opacity);
            Assert(actual == expected,
                $"normal blend mismatch for dst={dst.Bgra:X8}, src={src.Bgra:X8}, opacity={opacity}: " +
                $"{actual.Bgra:X8} != {expected.Bgra:X8}");
        }

        using var first = new Surface(3, 2);
        using var second = new Surface(3, 2);
        for (int y = 0; y < first.Height; y++)
        for (int x = 0; x < first.Width; x++)
            first[x, y] = ColorBgra.FromUInt32((uint)random.NextInt64(0, 1L << 32));
        second.Clear(ColorBgra.FromBgra(50, 60, 70, 128));

        using var document = new Document(3, 2);
        var bottom = document.AddLayer("bottom");
        bottom.Surface.CopyFrom(first);
        using var oneLayer = document.Flatten();
        AssertSurfacesEqual(first, oneLayer, "first-layer composite copy");

        var top = document.AddLayer("top");
        top.Surface.CopyFrom(second);
        using var rendered = document.Flatten();
        for (int y = 0; y < rendered.Height; y++)
        for (int x = 0; x < rendered.Width; x++)
            Assert(rendered[x, y] == CompositeNormalReference(first[x, y], second[x, y], 255),
                $"multi-layer composite mismatch at ({x},{y})");
    }

    private static unsafe void BlendSpansAndDirtyRectsStayExact()
    {
        var random = new Random(8128);
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            using var source = new Surface(257, 1);
            using var expected = new Surface(257, 1);
            using var actual = new Surface(257, 1);
            int opacity = random.Next(1, 256);
            for (int x = 0; x < source.Width; x++)
            {
                source[x, 0] = ColorBgra.FromUInt32((uint)random.NextInt64(0, 1L << 32));
                expected[x, 0] = actual[x, 0] =
                    ColorBgra.FromUInt32((uint)random.NextInt64(0, 1L << 32));
                expected[x, 0] = Blending.Composite(mode, expected[x, 0], source[x, 0], opacity);
            }
            Blending.CompositeSpan(mode, (ColorBgra*)actual.GetRowPointer(0),
                (ColorBgra*)source.GetRowPointer(0), source.Width, opacity);
            AssertSurfacesEqual(expected, actual, $"{mode} row blend");
        }

        using var document = new Document(11, 9);
        var bottom = document.AddLayer("bottom");
        var top = document.AddLayer("top");
        top.BlendMode = BlendMode.Overlay;
        top.Opacity = 173;
        for (int y = 0; y < document.Height; y++)
        for (int x = 0; x < document.Width; x++)
        {
            bottom.Surface[x, y] = ColorBgra.FromUInt32((uint)random.NextInt64(0, 1L << 32));
            top.Surface[x, y] = ColorBgra.FromUInt32((uint)random.NextInt64(0, 1L << 32));
        }
        using var full = document.Flatten();
        using var partial = new Surface(document.Width, document.Height);
        var sentinel = ColorBgra.FromBgra(7, 9, 11, 13);
        partial.Clear(sentinel);
        document.RenderTo(partial, 3, 2, 5, 4);
        for (int y = 0; y < document.Height; y++)
        for (int x = 0; x < document.Width; x++)
            Assert(partial[x, y] == (x >= 3 && x < 8 && y >= 2 && y < 6 ? full[x, y] : sentinel),
                $"dirty composite touched the wrong pixel at ({x},{y})");
    }

    private static void IncrementalPolygonsAndCoverageStayExact()
    {
        var points = new (double X, double Y)[] { (2, 2), (12, 3), (14, 9), (8, 13), (1, 10) };
        var expected = new Selection(16, 16);
        expected.ReplaceWithPolygon(points);
        var incremental = new Selection(16, 16);
        for (int i = 2; i < points.Length; i++)
            incremental.TogglePolygon(new[] { points[0], points[i - 1], points[i] });
        Assert(expected.Mask.SequenceEqual(incremental.Mask), "incremental polygon fan changed fill parity");

        using var baseline = new Surface(20, 20);
        using var target = baseline.Clone();
        var color = ColorBgra.FromBgra(20, 40, 200, 96);
        var stroke = new SoftBrushStroke(20, 20);
        stroke.Dab(10, 10, 5, 1);
        stroke.Dab(10, 10, 5, 1);
        stroke.DabLine(5, 10, 15, 10, 5, 1);
        stroke.Flush(target, baseline, color);
        Assert(target[10, 10].A == color.A,
            $"overlapping stroke accumulated alpha: {target[10, 10].A} != {color.A}");

        using var rectangle = new Surface(20, 20);
        ShapeOps.DrawRectangle(rectangle, 3, 3, 16, 16, 2, color, antialias: false);
        Assert(rectangle[3, 3].A == color.A,
            $"shape corner accumulated alpha: {rectangle[3, 3].A} != {color.A}");
    }

    private static void HistoryResidentBytesStayExact()
    {
        var history = new HistoryStack { MemoryBudgetBytes = 0 };
        history.Push(new FixedMemento(10));
        history.Push(new FixedMemento(20));
        Assert(history.ResidentBytes == 30, "history push accounting mismatch");

        history.Undo();
        Assert(history.ResidentBytes == 30, "history undo accounting mismatch");
        history.Push(new FixedMemento(5)); // discards the 20-byte redo branch
        Assert(history.ResidentBytes == 15, "history redo-branch accounting mismatch");

        bool detached = false;
        history.Push(new DelegateMemento("dynamic", () => detached = true, () => detached = false,
            () => detached ? 100 : 0));
        Assert(history.ResidentBytes == 15, "dynamic memento initial accounting mismatch");
        history.Undo();
        Assert(history.ResidentBytes == 115, "dynamic memento undo accounting mismatch");
        history.Redo();
        Assert(history.ResidentBytes == 15, "dynamic memento redo accounting mismatch");

        history.Clear();
        Assert(history.ResidentBytes == 0, "history clear accounting mismatch");
    }

    private static void SelectionBoundsAndClipStayExact()
    {
        var selection = new Selection(8, 6);
        selection.ReplaceWithRectangle(2, 1, 6, 5);
        Assert(selection.GetBounds() == (2, 1, 4, 4), "rectangle bounds mismatch");
        Assert(selection.GetBounds() == (2, 1, 4, 4), "cached rectangle bounds mismatch");

        selection.Select(0, 0);
        Assert(selection.GetBounds() == (0, 0, 6, 5), "expanded selection bounds mismatch");

        var hole = new Selection(8, 6);
        hole.ReplaceWithRectangle(3, 2, 5, 4);
        selection.Combine(SelectionCombineMode.Subtract, hole);
        var selectedBeforeClip = new bool[selection.Width * selection.Height];
        for (int y = 0; y < selection.Height; y++)
        for (int x = 0; x < selection.Width; x++)
            selectedBeforeClip[y * selection.Width + x] = selection.IsSelected(x, y);

        using var original = new Surface(8, 6);
        using var edited = new Surface(8, 6);
        for (int y = 0; y < original.Height; y++)
        for (int x = 0; x < original.Width; x++)
        {
            original[x, y] = ColorBgra.FromUInt32((uint)(1 + y * original.Width + x));
            edited[x, y] = ColorBgra.White;
        }

        selection.Clip(edited, original);
        for (int y = 0; y < edited.Height; y++)
        for (int x = 0; x < edited.Width; x++)
        {
            ColorBgra expected = selectedBeforeClip[y * edited.Width + x] ? ColorBgra.White : original[x, y];
            Assert(edited[x, y] == expected, $"selection clip mismatch at ({x},{y})");
        }

        var empty = new Selection(4, 3);
        empty.SelectAll();
        var whole = new Selection(4, 3);
        whole.SelectAll();
        empty.Combine(SelectionCombineMode.Subtract, whole);
        Assert(empty.IsActive, "explicitly empty selection became inactive/all-editable");
        Assert(empty.GetBounds() == (0, 0, 0, 0), "explicitly empty selection bounds mismatch");
        for (int y = 0; y < empty.Height; y++)
        for (int x = 0; x < empty.Width; x++)
            Assert(!empty.IsSelected(x, y), "explicitly empty selection exposed an editable pixel");

        empty.Invert();
        for (int y = 0; y < empty.Height; y++)
        for (int x = 0; x < empty.Width; x++)
            Assert(empty.IsSelected(x, y), "inverting an empty selection did not select all");
        empty.SelectNone();
        Assert(!empty.IsActive && empty.IsSelected(0, 0), "SelectNone did not restore all-editable state");
    }

    private static void TiledRotationStaysExact()
    {
        foreach (var (width, height) in new[] { (1, 1), (3, 7), (33, 35), (64, 31) })
        foreach (bool clockwise in new[] { false, true })
        {
            using var source = new Surface(width, height);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                source[x, y] = ColorBgra.FromUInt32((uint)(1 + y * width + x));

            using var rotated = SurfaceOps.Rotate90(source, clockwise);
            Assert(rotated.Width == height && rotated.Height == width, "rotation dimension mismatch");
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int destinationX = clockwise ? height - 1 - y : y;
                int destinationY = clockwise ? x : width - 1 - x;
                Assert(rotated[destinationX, destinationY] == source[x, y],
                    $"rotation mismatch for {width}x{height}, clockwise={clockwise}, source=({x},{y})");
            }
        }
    }

    private static void RectangularCopyIsClippedAndExact()
    {
        using var source = new Surface(6, 5);
        using var destination = new Surface(6, 5);
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
            source[x, y] = ColorBgra.FromUInt32((uint)(1 + y * source.Width + x));
        destination.Clear(ColorBgra.White);
        destination.CopyRectFrom(source, -2, 1, 6, 3);

        for (int y = 0; y < destination.Height; y++)
        for (int x = 0; x < destination.Width; x++)
        {
            ColorBgra expected = y >= 1 && y < 4 && x < 4 ? source[x, y] : ColorBgra.White;
            Assert(destination[x, y] == expected, $"rectangular copy mismatch at ({x},{y})");
        }
    }

    private static void LutEffectsStayExact()
    {
        CheckLut(new InvertEffect(), value => (byte)(255 - value), "invert LUT");

        const int brightness = 23;
        const double contrast = 1.37;
        CheckLut(new BrightnessContrastEffect(brightness, contrast),
            value => ClampByte((value - 128) * contrast + 128 + brightness), "brightness LUT");

        var curve = new byte[256];
        for (int value = 0; value < curve.Length; value++) curve[value] = (byte)(value ^ 0xA5);
        CheckLut(new CurvesEffect(curve), value => curve[value], "curves LUT");

        const int levels = 7;
        CheckLut(new PosterizeEffect(levels),
            value => (byte)((value * (levels - 1) / 255) * 255 / (levels - 1)), "posterize LUT");

        const int inputBlack = 17, inputWhite = 229;
        const double gamma = 1.6;
        CheckLut(new LevelsEffect(inputBlack, inputWhite, gamma), value =>
        {
            double amount = Math.Clamp((double)(value - inputBlack) / (inputWhite - inputBlack), 0, 1);
            return ClampByte(Math.Pow(amount, 1.0 / gamma) * 255);
        }, "levels LUT");
    }

    private static void RadialBlurPrecomputationStaysExact()
    {
        using var source = new Surface(9, 7);
        var random = new Random(901);
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
            source[x, y] = ColorBgra.FromUInt32((uint)random.NextInt64(0, 1L << 32));

        using var expected = source.Clone();
        using var actual = source.Clone();
        ApplyRadialReference(expected, 17, 1);
        new RadialBlurEffect(17, 1).Apply(actual);
        AssertSurfacesNear(expected, actual, 2, "radial blur precomputation");
    }

    private static void CheckLut(IEffect effect, Func<byte, byte> transform, string operation)
    {
        using var surface = new Surface(256, 1);
        for (int value = 0; value < 256; value++)
            surface[value, 0] = ColorBgra.FromBgra((byte)value, (byte)value, (byte)value, (byte)(255 - value));

        effect.Apply(surface);
        for (int value = 0; value < 256; value++)
        {
            byte expected = transform((byte)value);
            ColorBgra actual = surface[value, 0];
            Assert(actual.B == expected && actual.G == expected && actual.R == expected && actual.A == 255 - value,
                $"{operation} mismatch at {value}: {actual}");
        }
    }

    private static unsafe void ApplyRadialReference(Surface surface, double angleDegrees, int quality)
    {
        using var source = surface.Clone();
        int width = surface.Width, height = surface.Height;
        double centerX = width / 2.0, centerY = height / 2.0;
        int sampleCount = quality * quality * 8 + 8;
        double totalRadians = angleDegrees * Math.PI / 180.0;

        for (int y = 0; y < height; y++)
        {
            ColorBgra* destination = (ColorBgra*)surface.GetRowPointer(y);
            for (int x = 0; x < width; x++)
            {
                double dx = x - centerX, dy = y - centerY;
                double radius = Math.Sqrt(dx * dx + dy * dy);
                double baseAngle = Math.Atan2(dy, dx);
                double red = 0, green = 0, blue = 0, alpha = 0;
                int samples = 0;

                for (int i = 0; i <= sampleCount; i++)
                {
                    double amount = (double)i / sampleCount - 0.5;
                    double angle = baseAngle + amount * totalRadians;
                    double sampleX = centerX + radius * Math.Cos(angle);
                    double sampleY = centerY + radius * Math.Sin(angle);
                    const double edgeEpsilon = 1e-9;
                    if (sampleX < -edgeEpsilon || sampleY < -edgeEpsilon ||
                        sampleX > width - 1 + edgeEpsilon || sampleY > height - 1 + edgeEpsilon) continue;
                    sampleX = Math.Clamp(sampleX, 0, width - 1);
                    sampleY = Math.Clamp(sampleY, 0, height - 1);
                    ColorBgra color = source.GetBilinearSampleClamped((float)sampleX, (float)sampleY);
                    red += color.R * color.A;
                    green += color.G * color.A;
                    blue += color.B * color.A;
                    alpha += color.A;
                    samples++;
                }

                destination[x] = alpha > 0
                    ? ColorBgra.FromBgra(ClampByte(blue / alpha), ClampByte(green / alpha),
                        ClampByte(red / alpha), ClampByte(alpha / samples))
                    : ColorBgra.Transparent;
            }
        }
    }

    private static byte ClampByte(double value) =>
        (byte)Math.Clamp((int)(value + 0.5), 0, 255);

    private static ColorBgra CompositeNormalReference(ColorBgra dst, ColorBgra src, int opacity)
    {
        int effectiveAlpha = src.A * opacity / 255;
        if (effectiveAlpha == 0) return dst;

        double sourceAlpha = effectiveAlpha / 255.0;
        double backdropAlpha = dst.A / 255.0;
        double outputAlpha = sourceAlpha + backdropAlpha * (1 - sourceAlpha);

        byte Mix(int source, int destination)
        {
            double premultiplied = sourceAlpha * (1 - backdropAlpha) * source
                + sourceAlpha * backdropAlpha * source
                + (1 - sourceAlpha) * backdropAlpha * destination;
            return (byte)Math.Clamp((int)(premultiplied / outputAlpha + 0.5), 0, 255);
        }

        return ColorBgra.FromBgra(Mix(src.B, dst.B), Mix(src.G, dst.G), Mix(src.R, dst.R),
            (byte)(outputAlpha * 255 + 0.5));
    }

    private static void AssertSurfacesEqual(Surface expected, Surface actual, string operation)
    {
        Assert(expected.Width == actual.Width && expected.Height == actual.Height,
            $"{operation} dimension mismatch");
        for (int y = 0; y < expected.Height; y++)
        for (int x = 0; x < expected.Width; x++)
            Assert(expected[x, y] == actual[x, y], $"{operation} mismatch at ({x},{y})");
    }

    private static void AssertSurfacesNear(Surface expected, Surface actual, int tolerance, string operation)
    {
        Assert(expected.Width == actual.Width && expected.Height == actual.Height,
            $"{operation} dimension mismatch");
        for (int y = 0; y < expected.Height; y++)
        for (int x = 0; x < expected.Width; x++)
        {
            ColorBgra a = expected[x, y], b = actual[x, y];
            int delta = Math.Max(Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.G - b.G)),
                Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.A - b.A)));
            Assert(delta <= tolerance, $"{operation} mismatch at ({x},{y}), delta={delta}: {a} vs {b}");
        }
    }

    private sealed class FixedMemento(long bytes) : HistoryMemento("fixed")
    {
        public override long ApproximateBytes => bytes;
        public override HistoryMemento Undo() => this;
    }

    private static void AssertEveryPixel(Surface surface, ColorBgra expected, string operation)
    {
        for (int y = 0; y < surface.Height; y++)
        for (int x = 0; x < surface.Width; x++)
            Assert(surface[x, y] == expected, $"{operation} mismatch at ({x},{y})");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
