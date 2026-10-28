using KawaPaint.Engine;
using KawaPaint.Engine.ThreeD;
using Xunit;

namespace KawaPaint.Tests;

public class LiveModelLayerTests
{
    // An asymmetric, UV-mapped wedge: a unit square facing the camera plus a spike, so turns,
    // flips and offsets all show in the silhouette.
    private const string Wedge = """
        v -1 -1 0
        v  1 -1 0
        v  1  1 0
        v -1  1 0
        v  1.8 0.4 0.6
        vt 0 0
        vt 1 0
        vt 1 1
        vt 0 1
        f 1/1 2/2 3/3 4/4
        f 2/2 5/3 3/3
        """;

    private static ObjMesh WedgeMesh() => ObjMesh.Parse(new StringReader(Wedge));

    private static LiveModelSettings Front(int width, int height) =>
        LiveModelSettings.Centered(width, height) with { YawDegrees = 0, PitchDegrees = 0, Diameter = 30 };

    private static (Document Doc, Layer Live) LiveDocument(int width = 64, int height = 48, LiveModelSettings? settings = null)
    {
        var doc = new Document(width, height);
        doc.AddLayer(new Layer(width, height, "Background"));
        var model = new LiveModel(WedgeMesh(), "wedge.obj", settings ?? Front(width, height));
        var layer = new Layer(LiveModelRenderer.Render(model, width, height, null), "Wedge") { LiveModel = model };
        doc.AddLayer(layer);
        return (doc, layer);
    }

    private static int AlphaMismatches(Surface a, Surface b, int threshold = 128)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        int count = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a[x, y].A >= threshold != b[x, y].A >= threshold) count++;
        return count;
    }

    private static int Coverage(Surface s)
    {
        int count = 0;
        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
                if (s[x, y].A >= 128) count++;
        return count;
    }

    private static (int Left, int Top, int Right, int Bottom) Bounds(Surface s)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
                if (s[x, y].A >= 128)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
        return (left, top, right, bottom);
    }

    [Fact]
    public void FixedPlacementKeepsTheModelInsideItsDiameterAtEveryAngle()
    {
        ObjMesh mesh = WedgeMesh();
        foreach (float yaw in new[] { 0f, 45f, 90f, 170f })
        {
            var settings = new LiveModelSettings { YawDegrees = yaw, PitchDegrees = 20, CenterX = 50, CenterY = 40, Diameter = 40 };
            using Surface render = LiveModelRenderer.Render(new LiveModel(mesh, "m", settings), 100, 80, null);
            var (left, top, right, bottom) = Bounds(render);
            Assert.True(left >= 29 && right <= 70 && top >= 19 && bottom <= 60, $"yaw {yaw}: {left},{top}-{right},{bottom}");
        }
    }

    [Fact]
    public void PixelEditRasterizesTheLayerAndUndoMakesItLiveAgain()
    {
        var (doc, layer) = LiveDocument();
        LiveModel model = layer.LiveModel!;
        using Surface before = layer.Surface.Clone();
        layer.Surface[1, 1] = ColorBgra.FromBgra(0, 0, 255, 255);

        var history = new HistoryStack();
        history.Push(TileDeltaMemento.Create(layer, before, "Pencil"));
        Assert.Null(layer.LiveModel);

        history.Undo();
        Assert.Same(model, layer.LiveModel);
        Assert.Equal(0, layer.Surface[1, 1].A);

        history.Redo();
        Assert.Null(layer.LiveModel);
        Assert.Equal(255, layer.Surface[1, 1].A);
        doc.Dispose();
    }

    [Fact]
    public void WholeLayerEditRasterizesTooAndNoOpEditLeavesTheLayerLive()
    {
        var (doc, layer) = LiveDocument();
        LiveModel model = layer.LiveModel!;

        using (Surface same = layer.Surface.Clone())
            Assert.Null(TileDeltaMemento.Create(layer, same, "Nothing"));
        Assert.Same(model, layer.LiveModel);

        var history = new HistoryStack();
        history.Push(new LayerSurfaceMemento(layer, "Invert"));
        Assert.Null(layer.LiveModel);
        history.Undo();
        Assert.Same(model, layer.LiveModel);
        doc.Dispose();
    }

    [Fact]
    public void SettingsChangeIsOneUndoStepRestoringModelAndPixels()
    {
        var (doc, layer) = LiveDocument();
        LiveModel before = layer.LiveModel!;
        using Surface pixelsBefore = layer.Surface.Clone();

        var history = new HistoryStack();
        history.Push(new LiveModelMemento(layer, "3D Layer Properties"));
        layer.LiveModel = before.WithSettings(before.Settings with { YawDegrees = 60, CenterX = 20 });
        LiveModelRenderer.Refresh(doc, layer);
        Assert.NotEqual(0, AlphaMismatches(pixelsBefore, layer.Surface));

        history.Undo();
        Assert.Same(before, layer.LiveModel);
        Assert.Equal(0, AlphaMismatches(pixelsBefore, layer.Surface, 1));

        history.Redo();
        Assert.Equal(60, layer.LiveModel!.Settings.YawDegrees);
        doc.Dispose();
    }

    [Fact]
    public void TextureLinkWearsTheLinkedLayerButNeverItself()
    {
        var (doc, layer) = LiveDocument();
        Layer texture = doc.Layers[0];
        texture.Surface.Clear(ColorBgra.FromBgra(0, 0, 255, 255));
        layer.LiveModel = layer.LiveModel! with
        {
            Settings = layer.LiveModel.Settings with { TextureLayerId = texture.Id, Ambient = 1 }
        };
        LiveModelRenderer.Refresh(doc, layer);
        ColorBgra center = layer.Surface[32, 24];
        Assert.True(center.R > 200 && center.B < 40, center.ToString());

        layer.LiveModel = layer.LiveModel with { Settings = layer.LiveModel.Settings with { TextureLayerId = layer.Id } };
        Assert.Null(LiveModelRenderer.ResolveTexture(doc, layer));
        doc.Dispose();
    }

    [Fact]
    public void CropAndCanvasSizeKeepTheLayerLiveAtTheSameSpot()
    {
        var (doc, layer) = LiveDocument();
        using Document cropped = DocumentOps.Crop(doc, 10, 6, 40, 30);
        Layer croppedLayer = cropped.Layers[1];
        Assert.NotNull(croppedLayer.LiveModel);
        Assert.Equal(layer.Id, croppedLayer.Id);
        using Surface expected = layer.Surface.Crop(10, 6, 40, 30);
        Assert.True(AlphaMismatches(expected, croppedLayer.Surface) <= 2);

        using Document padded = DocumentOps.ResizeCanvas(doc, 80, 60, CanvasAnchor.BottomRight);
        Assert.Equal(layer.LiveModel!.Settings.CenterX + 16, padded.Layers[1].LiveModel!.Settings.CenterX);
        Assert.Equal(Coverage(layer.Surface), Coverage(padded.Layers[1].Surface));
        doc.Dispose();
    }

    [Fact]
    public void ResizeRerendersSharpInsteadOfResampling()
    {
        var (doc, layer) = LiveDocument();
        using Document bigger = DocumentOps.Resize(doc, 128, 96);
        Layer resized = bigger.Layers[1];
        Assert.Equal(60, resized.LiveModel!.Settings.Diameter, 3);
        using Surface fresh = LiveModelRenderer.Render(resized.LiveModel, 128, 96, null);
        Assert.Equal(0, AlphaMismatches(fresh, resized.Surface, 1));
        doc.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rotate90TurnsTheModelWithTheCanvas(bool clockwise)
    {
        var (doc, layer) = LiveDocument(64, 48, Front(64, 48) with { CenterX = 22, CenterY = 20 });
        using Surface turnedPixels = SurfaceOps.Rotate90(layer.Surface, clockwise);
        using Document turned = DocumentOps.Rotate90(doc, clockwise);
        Assert.NotNull(turned.Layers[1].LiveModel);
        Assert.True(AlphaMismatches(turnedPixels, turned.Layers[1].Surface) <= 12,
            AlphaMismatches(turnedPixels, turned.Layers[1].Surface).ToString());
        doc.Dispose();
    }

    [Fact]
    public void FlipsAndHalfTurnMatchThePixelTransformAndCompose()
    {
        var (doc, layer) = LiveDocument(64, 48, Front(64, 48) with { CenterX = 24, CenterY = 18, YawDegrees = 20 });
        using Surface original = layer.Surface.Clone();

        using (Surface flipped = original.Clone())
        {
            SurfaceOps.FlipHorizontal(flipped);
            DocumentOps.FlipHorizontal(doc);
            Assert.True(layer.LiveModel!.Settings.MirrorX);
            Assert.True(AlphaMismatches(flipped, layer.Surface) <= 6);
        }

        // Flipping back restores the original placement exactly.
        DocumentOps.FlipHorizontal(doc);
        Assert.False(layer.LiveModel!.Settings.MirrorX);
        Assert.Equal(0, AlphaMismatches(original, layer.Surface, 1));

        DocumentOps.Rotate180(doc);
        using Surface halfTurn = original.Clone();
        SurfaceOps.FlipHorizontal(halfTurn);
        SurfaceOps.FlipVertical(halfTurn);
        Assert.True(AlphaMismatches(halfTurn, layer.Surface) <= 6);
        doc.Dispose();
    }

    [Fact]
    public void ProjectRoundTripKeepsTheLayerLiveAndSharesMeshesAcrossFrames()
    {
        var (doc, layer) = LiveDocument();
        Layer texture = doc.Layers[0];
        layer.LiveModel = layer.LiveModel! with
        {
            Settings = layer.LiveModel.Settings with { TextureLayerId = texture.Id, RollDegrees = 12, MirrorY = true }
        };
        LiveModelRenderer.Refresh(doc, layer);
        doc.AddFrame(cloneCurrent: true);

        using var stream = new MemoryStream();
        DocumentFile.Save(doc, stream, TestContext.Current.CancellationToken);
        stream.Position = 0;
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true))
            Assert.Single(zip.Entries, e => e.FullName.StartsWith("models/", StringComparison.Ordinal));
        stream.Position = 0;
        using Document loaded = DocumentFile.Load(stream);

        foreach (DocumentFrame frame in loaded.Frames)
        {
            Layer live = frame.Layers[1];
            Assert.NotNull(live.LiveModel);
            Assert.Equal(layer.LiveModel.Settings, live.LiveModel!.Settings);
            Assert.Equal("wedge.obj", live.LiveModel.SourceName);
            Assert.Equal(texture.Id, frame.Layers[0].Id);
            Assert.Equal(WedgeMesh().Triangles, live.LiveModel.Mesh.Triangles);
        }
        Assert.Same(loaded.Frames[0].Layers[1].LiveModel!.Mesh, loaded.Frames[1].Layers[1].LiveModel!.Mesh);
        doc.Dispose();
    }

    [Fact]
    public void ExplodedProjectRoundTripKeepsTheLayerLive()
    {
        var (doc, _) = LiveDocument();
        string dir = Path.Combine(Path.GetTempPath(), "kp-live3d-" + Guid.NewGuid().ToString("N"));
        try
        {
            DocumentFile.SaveExploded(doc, dir);
            Assert.True(File.Exists(Path.Combine(dir, "models", "0.kpmesh")));
            using Document loaded = DocumentFile.LoadExploded(dir);
            Assert.NotNull(loaded.Layers[1].LiveModel);
        }
        finally
        {
            doc.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DamagedMeshOpensTheLayerAsPlainPixels()
    {
        var (doc, layer) = LiveDocument();
        using var original = new MemoryStream();
        DocumentFile.Save(doc, original, TestContext.Current.CancellationToken);

        using var damaged = new MemoryStream();
        original.Position = 0;
        using (var source = new System.IO.Compression.ZipArchive(original, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true))
        using (var target = new System.IO.Compression.ZipArchive(damaged, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var output = target.CreateEntry(entry.FullName).Open();
                if (entry.FullName.StartsWith("models/", StringComparison.Ordinal)) output.Write(new byte[] { 1, 2, 3 });
                else { using var input = entry.Open(); input.CopyTo(output); }
            }
        }
        damaged.Position = 0;
        using Document loaded = DocumentFile.Load(damaged);
        Assert.Null(loaded.Layers[1].LiveModel);
        Assert.Equal(0, AlphaMismatches(layer.Surface, loaded.Layers[1].Surface, 1));
        doc.Dispose();
    }

    [Fact]
    public void MeshBinaryRejectsTruncatedData()
    {
        using var stream = new MemoryStream();
        MeshBinary.Write(WedgeMesh(), stream);
        byte[] bytes = stream.ToArray();
        Assert.Throws<InvalidDataException>(() => MeshBinary.Read(new MemoryStream(bytes[..^5])));
    }
}
