using KawaPaint.Engine;

namespace KawaPaint.Sandbox;

/// <summary>Proves that bounded rendering is the same operation as a full render, merely clipped
/// to the requested destination rectangle. This is the contract live previews rely on.</summary>
internal static class EffectBoundsSmokeTest
{
    public static void RunAll()
    {
        EffectBounds roi = new(5, 4, 17, 13);
        using Surface source = Pattern(31, 23);

        foreach (IEffect effect in Effects())
        {
            using Surface full = source.Clone();
            using Surface partial = source.Clone();
            effect.Apply(full);
            effect.Apply(partial, roi);

            for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                bool inside = x >= roi.X && x < roi.Right && y >= roi.Y && y < roi.Bottom;
                ColorBgra expected = inside ? full[x, y] : source[x, y];
                if (partial[x, y] != expected)
                    throw new InvalidOperationException(
                        $"EFFECT BOUNDS SMOKE FAILED: {effect.Name} differs at ({x},{y}), inside={inside}");
            }
        }

        using (Surface legacy = source.Clone())
        {
            IEffect legacyEffect = new LegacyFillEffect();
            legacyEffect.Apply(legacy, roi);
            for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                bool inside = x >= roi.X && x < roi.Right && y >= roi.Y && y < roi.Bottom;
                ColorBgra expected = inside ? ColorBgra.White : source[x, y];
                if (legacy[x, y] != expected)
                    throw new InvalidOperationException("EFFECT BOUNDS SMOKE FAILED: legacy compatibility fallback escaped ROI");
            }
        }

        Surface disposed = new(1, 1);
        disposed.Dispose();
        ExpectDisposed(() => _ = disposed[0, 0], "getter");
        ExpectDisposed(() => disposed[0, 0] = ColorBgra.Black, "setter");

        Console.WriteLine($"EFFECT BOUNDS SMOKE OK - {Effects().Length} built-ins + legacy fallback, disposed indexer guarded");
    }

    private static IEffect[] Effects() =>
    [
        new InvertEffect(), new GrayscaleEffect(), new SepiaEffect(),
        new BrightnessContrastEffect(12, 1.15), new HueSaturationEffect(30, 1.2, 0.1),
        new LevelsEffect(10, 240, 1.1), new AutoLevelsEffect(), new BoxBlurEffect(3),
        new SharpenEffect(), new EmbossEffect(), new EdgeDetectEffect(), new PosterizeEffect(5),
        new NoiseEffect(12, 12345), new CurvesEffect(Enumerable.Range(0, 256).Select(i => (byte)(255 - i)).ToArray()),
        new BulgeEffect(45), new TwistEffect(30, 1.0), new PolarInversionEffect(1.0),
        new TileEffect(30, 12, 8), new FrostedGlassEffect(0, 3, 3, 23456), new PixelateEffect(6),
        new MedianEffect(4), new OutlineEffect(3, 50), new ReliefEffect(45), new VignetteEffect(0.7, 0.5),
        new DentsEffect(25, 50, 10, 10, 34567), new MotionBlurEffect(25, 10),
        new RadialBlurEffect(15), new ZoomBlurEffect(20), new FragmentEffect(4, 0, 8),
        new SurfaceBlurEffect(4, 15), new UnfocusEffect(4), new ReduceNoiseEffect(6, 0.4),
        new CloudsEffect(120, 0.5, 45678, ColorBgra.Black, ColorBgra.White),
        new JuliaFractalEffect(4.0, 1.0, 0), new MandelbrotFractalEffect(1, 10, 0, true),
        new GlowEffect(6, 10, 10), new RedEyeRemoveEffect(70, 90), new SoftenPortraitEffect(5, 0, 10),
        new InkSketchEffect(50, 50), new PencilSketchEffect(2, 0), new OilPaintingEffect(3, 50)
    ];

    private static Surface Pattern(int width, int height)
    {
        var surface = new Surface(width, height);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            surface[x, y] = ColorBgra.FromBgra(
                (byte)((x * 17 + y * 3) & 255), (byte)((x * 5 + y * 19) & 255),
                (byte)((x * 11 + y * 7) & 255), (byte)(80 + ((x * 13 + y * 23) % 176)));
        return surface;
    }

    private static void ExpectDisposed(Action action, string access)
    {
        try
        {
            action();
            throw new InvalidOperationException($"SURFACE INDEXER SMOKE FAILED: disposed {access} did not throw");
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Represents a binary plugin compiled for the original one-argument contract.</summary>
    private sealed class LegacyFillEffect : IEffect
    {
        public string Name => "Legacy fill";
        public void Apply(Surface surface) => surface.Clear(ColorBgra.White);
    }
}
