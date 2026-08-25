// KawaPaint - tag -> IEffect for headless script execution. A deliberate hand-transcribed copy of
// the two switches in shared\KawaPaint.App\MainView.axaml.cs (OnEffect's parameterless tags,
// OnAdjust's parametric ones), not a shared table with them: Plugins\EffectRegistry.cs already
// documents this project's convention that built-in effects "stay on their existing hardcoded
// switch in MainView.axaml.cs" rather than being centralized, and reaching into that switch from
// here would mean touching the live, tested preview-dialog code path for no v1 benefit. If either
// switch's constructor args/casts change, this one needs the matching edit by hand.
//
// "clouds" has no case here: its MainView factory reads the live foreground/background color
// (Canvas.BrushColor/SecondaryColor), which a headless target document has no equivalent for.

namespace KawaPaint.Engine.Scripting;

public static class ScriptEffects
{
    /// <summary>True for every effect tag a script can carry - the effect half of
    /// <see cref="Core.Scripting.ScriptRecorder"/>'s (App-side) allow-list, kept here since it's
    /// the same set <see cref="Build"/> knows how to construct.</summary>
    public static bool IsKnownTag(string tag) => tag switch
    {
        "invert" or "gray" or "sepia" or "sharpen" or "emboss" or "edge" or "autolevels" => true,
        "bc" or "hsl" or "levels" or "blur" or "posterize" or "noise" or "bulge" or "twist"
            or "polarinv" or "tile" or "frostedglass" or "pixelate" or "dents" or "median"
            or "outline" or "relief" or "vignette" or "reducenoise" or "motionblur" or "radialblur"
            or "zoomblur" or "surfaceblur" or "unfocus" or "fragment" or "julia" or "mandelbrot"
            or "glow" or "redeye" or "softenportrait" or "inksketch" or "pencilsketch"
            or "oilpainting" or "clouds" => true,
        _ => false
    };

    /// <summary>Builds the effect for a tag + committed args, or null if the tag is unknown or the
    /// args don't match what that tag expects (wrong count - e.g. a hand-edited .kpscript).</summary>
    public static IEffect? Build(string tag, IReadOnlyList<double> a, IReadOnlyList<string>? strings = null)
    {
        try
        {
            return tag switch
            {
                // parameterless - from OnEffect (MainView.axaml.cs)
                "invert" => new InvertEffect(),
                "gray" => new GrayscaleEffect(),
                "sepia" => new SepiaEffect(),
                "sharpen" => new SharpenEffect(),
                "emboss" => new EmbossEffect(),
                "edge" => new EdgeDetectEffect(),
                "autolevels" => new AutoLevelsEffect(),

                // parametric - from OnAdjust (MainView.axaml.cs)
                "bc" => new BrightnessContrastEffect((int)a[0], a[1]),
                "hsl" => new HueSaturationEffect(a[0], a[1], a[2]),
                "levels" => new LevelsEffect((int)a[0], (int)a[1], a[2]),
                "blur" => new BoxBlurEffect((int)a[0]),
                "posterize" => new PosterizeEffect((int)a[0]),
                "noise" => new NoiseEffect((int)a[0]),
                "bulge" => new BulgeEffect(a[0]),
                "twist" => new TwistEffect(a[0], a[1]),
                "polarinv" => new PolarInversionEffect(a[0]),
                "tile" => new TileEffect(a[0], a[1], a[2]),
                "frostedglass" => new FrostedGlassEffect(a[0], a[1], (int)a[2]),
                "pixelate" => new PixelateEffect((int)a[0]),
                "dents" => new DentsEffect(a[0], a[1], a[2], a[3], 0),
                "median" => new MedianEffect((int)a[0], (int)a[1]),
                "outline" => new OutlineEffect((int)a[0], (int)a[1]),
                "relief" => new ReliefEffect(a[0]),
                "vignette" => new VignetteEffect(a[0], a[1]),
                "reducenoise" => new ReduceNoiseEffect((int)a[0], a[1]),
                "motionblur" => new MotionBlurEffect(a[0], (int)a[1]),
                "radialblur" => new RadialBlurEffect(a[0]),
                "zoomblur" => new ZoomBlurEffect((int)a[0]),
                "surfaceblur" => new SurfaceBlurEffect((int)a[0], (int)a[1]),
                "unfocus" => new UnfocusEffect((int)a[0]),
                "fragment" => new FragmentEffect((int)a[0], a[1], (int)a[2]),
                "clouds" when strings is { Count: >= 2 }
                    => new CloudsEffect((int)a[0], a[1], 0,
                        ColorBgra.ParseHexString(strings[0]), ColorBgra.ParseHexString(strings[1])),
                "julia" => new JuliaFractalEffect(a[0], a[1], a[2]),
                "mandelbrot" => new MandelbrotFractalEffect((int)a[0], a[1], a[2]),
                "glow" => new GlowEffect((int)a[0], (int)a[1], (int)a[2]),
                "redeye" => new RedEyeRemoveEffect((int)a[0], (int)a[1]),
                "softenportrait" => new SoftenPortraitEffect((int)a[0], (int)a[1], (int)a[2]),
                "inksketch" => new InkSketchEffect((int)a[0], (int)a[1]),
                "pencilsketch" => new PencilSketchEffect((int)a[0], (int)a[1]),
                "oilpainting" => new OilPaintingEffect((int)a[0], (int)a[1]),

                _ => null
            };
        }
        catch (IndexOutOfRangeException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (FormatException) { return null; }
    }
}
