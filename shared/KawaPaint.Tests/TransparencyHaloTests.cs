using KawaPaint.Engine;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>
/// Issue #8: averaging colors without weighting them by alpha drags the color of transparent
/// pixels (black) into the result, so a red shape on a transparent layer grows a dark fringe.
/// Every effect here only moves or mixes existing pixels, so the output must stay pure red
/// wherever it is visible.
/// </summary>
public class TransparencyHaloTests
{
    public static TheoryData<string> Effects => new(EffectsByName.Keys);

    private static readonly Dictionary<string, Func<IEffect>> EffectsByName = new()
    {
        ["Blur"] = () => new BoxBlurEffect(6),
        ["Gaussian Blur"] = () => new GaussianBlurEffect(6),
        ["Motion Blur"] = () => new MotionBlurEffect(25, 12),
        ["Radial Blur"] = () => new RadialBlurEffect(30),
        ["Zoom Blur"] = () => new ZoomBlurEffect(40),
        ["Surface Blur"] = () => new SurfaceBlurEffect(5, 50),
        ["Unfocus"] = () => new UnfocusEffect(5),
        ["Median"] = () => new MedianEffect(4),
        ["Frosted Glass"] = () => new FrostedGlassEffect(2, 6, 4, seed: 1),
        ["Fragment"] = () => new FragmentEffect(4, 0, 6),
        ["Bulge"] = () => new BulgeEffect(60),
        ["Twist"] = () => new TwistEffect(30, 1),
        ["Pixelate"] = () => new PixelateEffect(6),
    };

    [Theory]
    [MemberData(nameof(Effects))]
    public void EffectKeepsTheColorOfAShapeOnTransparency(string name)
    {
        using var surface = RedDiscOnTransparent();
        EffectsByName[name]().Apply(surface);
        AssertNoDarkFringe(surface);
    }

    [Fact]
    public void ResizeKeepsTheColorOfAShapeOnTransparency()
    {
        using var surface = RedDiscOnTransparent();
        using var smaller = surface.Resized(29, 29);
        using var larger = surface.Resized(173, 173);
        AssertNoDarkFringe(smaller);
        AssertNoDarkFringe(larger);
    }

    [Fact]
    public void ResizeKeepsAUniformTranslucentColor()
    {
        using var surface = new Surface(100, 100);
        surface.Clear(ColorBgra.FromBgra(0, 0, 255, 128));
        using var resized = surface.Resized(37, 61);
        ColorBgra c = resized[10, 10];
        Assert.InRange(c.R, 250, 255);
        Assert.InRange(c.A, 126, 130);
    }

    /// <summary>Issue #15: the Gaussian Blur menu entry used a box blur, whose flat kernel turns a
    /// point into a hard-edged square. A Gaussian falls off smoothly and keeps it round.</summary>
    [Fact]
    public void GaussianBlurFallsOffSmoothlyAndRoundly()
    {
        using var surface = new Surface(41, 41);
        surface.Clear(ColorBgra.Black);
        surface[20, 20] = ColorBgra.White;
        new GaussianBlurEffect(9).Apply(surface);

        for (int x = 21; x <= 29; x++)
            Assert.True(surface[x, 20].R <= surface[x - 1, 20].R, $"brightness rises again at x={x}");
        Assert.True(surface[26, 20].R > surface[29, 20].R, "falloff is flat, like a box blur");
        // Same distance along the axis and the diagonal: a box kernel makes the diagonal as bright
        // as the axis (a square), a Gaussian makes it darker (a circle).
        Assert.True(surface[24, 24].R < surface[24, 20].R, "blurred point is square, not round");
    }

    private static Surface RedDiscOnTransparent()
    {
        var surface = new Surface(80, 80);
        for (int y = 0; y < 80; y++)
            for (int x = 0; x < 80; x++)
                if ((x - 40) * (x - 40) + (y - 40) * (y - 40) <= 18 * 18)
                    surface[x, y] = ColorBgra.FromBgra(0, 0, 255, 255);
        return surface;
    }

    private static void AssertNoDarkFringe(Surface surface)
    {
        int visible = 0;
        for (int y = 0; y < surface.Height; y++)
            for (int x = 0; x < surface.Width; x++)
            {
                ColorBgra c = surface[x, y];
                if (c.A < 24) continue;   // too faint for 8-bit color to be meaningful
                visible++;
                Assert.True(c.R >= 235 && c.G <= 20 && c.B <= 20,
                    $"pixel ({x},{y}) is {c} - a dark fringe instead of red");
            }
        Assert.True(visible > 0, "the effect erased the shape");
    }
}
