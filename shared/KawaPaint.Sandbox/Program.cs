using KawaPaint.Engine;
using SkiaSharp;

// Broad smoke test: touch every engine area once; fail loudly on any exception.
string tmp = Path.Combine(Path.GetTempPath(), "kawa_smoke");
Directory.CreateDirectory(tmp);

using var doc = new Document(64, 48);
var bg = doc.AddLayer("bg");
bg.Surface.Clear(ColorBgra.White);

BrushOps.FillDisc(bg.Surface, 20, 20, 8, ColorBgra.Black, StampMode.Blend, true);
BrushOps.DrawLine(bg.Surface, 0, 0, 63, 47, 2, ColorBgra.FromBgr(200, 0, 0), StampMode.Blend, true);
ShapeOps.DrawRectangle(bg.Surface, 5, 5, 40, 30, 1, ColorBgra.FromBgr(0, 150, 0));
ShapeOps.FillEllipse(bg.Surface, 30, 20, 55, 40, ColorBgra.FromBgra(0, 0, 255, 128));
BrushOps.CloneDisc(bg.Surface, bg.Surface, 50, 10, -30, 10, 5, true);
BrushOps.RecolorDisc(bg.Surface, 45, 5, 4, ColorBgra.White, ColorBgra.FromBgr(255, 220, 0), 40, true);
ShapeOps.FillRoundedRectangle(bg.Surface, 2, 34, 20, 46, 5, ColorBgra.FromBgra(0, 200, 0, 180));
ShapeOps.DrawRoundedRectangle(bg.Surface, 22, 34, 40, 46, 5, 1, ColorBgra.FromBgr(0, 0, 0), true);
ShapeOps.FillPolygon(bg.Surface, [(46.0, 34.0), (58.0, 38.0), (54.0, 46.0), (44.0, 44.0)], ColorBgra.FromBgra(200, 0, 200, 180));
GradientOps.LinearGradient(bg.Surface, 0, 0, 64, 0, ColorBgra.FromBgra(0, 0, 0, 60), ColorBgra.Transparent);
TextOps.DrawText(bg.Surface, "Hi", 4, 4, 14, ColorBgra.Black);
FloodFill.Fill(bg.Surface, 62, 46, ColorBgra.FromBgr(240, 240, 200), 20);

var top = doc.AddLayer("top");
top.BlendMode = BlendMode.Multiply; top.Opacity = 180;
top.Surface.Clear(ColorBgra.FromBgr(255, 200, 150));

var sel = new Selection(64, 48);
sel.ReplaceWithEllipse(10, 10, 54, 38);

IEffect[] effects =
{
    new InvertEffect(), new GrayscaleEffect(), new SepiaEffect(), new BrightnessContrastEffect(10, 1.1),
    new HueSaturationEffect(30, 1.2, 0.1), new LevelsEffect(10, 240, 1.1), new AutoLevelsEffect(),
    new BoxBlurEffect(3), new SharpenEffect(), new EmbossEffect(), new EdgeDetectEffect(),
    new PosterizeEffect(5), new NoiseEffect(10), new CurvesEffect(BuildIdentityLut()),
    new BulgeEffect(45), new TwistEffect(30, 1.0), new PolarInversionEffect(1.0),
    new TileEffect(30, 12, 8), new FrostedGlassEffect(0, 3, 2), new PixelateEffect(6),
    new MedianEffect(4), new OutlineEffect(3, 50), new ReliefEffect(45), new VignetteEffect(0.7, 0.5),
    new DentsEffect(25, 50, 10, 10, 0), new MotionBlurEffect(25, 10), new RadialBlurEffect(15),
    new ZoomBlurEffect(20), new FragmentEffect(4, 0, 8), new SurfaceBlurEffect(4, 15), new UnfocusEffect(4),
    new ReduceNoiseEffect(6, 0.4), new CloudsEffect(120, 0.5, 0, ColorBgra.White, ColorBgra.FromBgr(200, 120, 40)),
    new JuliaFractalEffect(4.0, 1.0, 0), new MandelbrotFractalEffect(1, 10, 0),
    new GlowEffect(6, 10, 10), new RedEyeRemoveEffect(70, 90), new SoftenPortraitEffect(5, 0, 10),
    new InkSketchEffect(50, 50), new PencilSketchEffect(2, 0), new OilPaintingEffect(3, 50)
};
foreach (var fx in effects)
{
    using var c = bg.Surface.Clone();
    fx.Apply(c);
    sel.Clip(c, bg.Surface);
}

var dup = top.Clone();
LayerOps.MergeInto(bg, top);

using var cropped = DocumentOps.Crop(doc, 5, 5, 40, 30);
using var resized = DocumentOps.Resize(doc, 32, 24);
DocumentOps.FlipHorizontal(doc);
DocumentOps.FlipVertical(doc);
using var rotated = DocumentOps.Rotate90(doc, true);
using var flatSurf = doc.Flatten();

string kwp = Path.Combine(tmp, "smoke.kwp");
DocumentFile.Save(doc, kwp);
using var reloaded = DocumentFile.Load(kwp);
flatSurf.Save(Path.Combine(tmp, "smoke.png"), SKEncodedImageFormat.Png);

Console.WriteLine($"SMOKE OK - effects={effects.Length}, reloaded {reloaded.Width}x{reloaded.Height} {reloaded.LayerCount} layer(s), dup='{dup.Name}'");

KawaPaint.Sandbox.PluginSmokeTest.RunAll();
KawaPaint.Sandbox.PdnPluginSmokeTest.RunAll();
KawaPaint.Sandbox.MetadataSmokeTest.RunAll();
KawaPaint.Sandbox.ExportPresetSmokeTest.RunAll();
KawaPaint.Sandbox.DemoFormatSmokeTest.RunAll();
KawaPaint.Sandbox.CliPresetSmokeTest.RunAll();
KawaPaint.Sandbox.MailMergeSmokeTest.RunAll();
KawaPaint.Sandbox.EngineOptimizationSmokeTest.RunAll();
KawaPaint.Sandbox.EffectBoundsSmokeTest.RunAll();
KawaPaint.Sandbox.DocumentLifecycleSmokeTest.RunAll();
KawaPaint.Sandbox.ScriptingV2SmokeTest.RunAll();
KawaPaint.Sandbox.AnimatedGifSmokeTest.RunAll();
KawaPaint.Sandbox.HistoryTruncateSmokeTest.RunAll();
KawaPaint.Sandbox.SelectionAntialiasSmokeTest.RunAll();
KawaPaint.Sandbox.ThreeDReferenceSmokeTest.RunAll();
KawaPaint.Sandbox.TabletInputSmokeTest.RunAll();
KawaPaint.Sandbox.PenPathSmokeTest.RunAll();
KawaPaint.Sandbox.ReferenceModelSmokeTest.RunAll();
KawaPaint.Sandbox.PublisherSmokeTest.RunAll();

static byte[] BuildIdentityLut() { var l = new byte[256]; for (int i = 0; i < 256; i++) l[i] = (byte)i; return l; }
