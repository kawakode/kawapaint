using System.Numerics;

namespace KawaPaint.Engine.ThreeD;

/// <summary>
/// Camera, placement and look of a live 3D layer. Positions and sizes are canvas pixels, so the
/// document geometry operations can map them exactly like any other canvas coordinate.
/// </summary>
public sealed record LiveModelSettings
{
    public float YawDegrees { get; init; } = -35;
    public float PitchDegrees { get; init; } = -25;
    public float RollDegrees { get; init; }

    /// <summary>Where the centre of the model's bounding box sits on the canvas.</summary>
    public float CenterX { get; init; }
    public float CenterY { get; init; }

    /// <summary>Width of the model's bounding sphere on the canvas; constant as the model turns.</summary>
    public float Diameter { get; init; } = 100;

    public bool MirrorX { get; init; }
    public bool MirrorY { get; init; }
    public float Ambient { get; init; } = 0.28f;
    public ColorBgra Color { get; init; } = ColorBgra.FromBgr(195, 205, 220);

    /// <summary>The layer (same frame) whose pixels the model wears on its UV-mapped faces, or null.</summary>
    public Guid? TextureLayerId { get; init; }

    /// <summary>Default framing for a new layer: centred, filling the canvas like the reference importer.</summary>
    public static LiveModelSettings Centered(int canvasWidth, int canvasHeight) => new()
    {
        CenterX = canvasWidth / 2f,
        CenterY = canvasHeight / 2f,
        Diameter = Math.Min(canvasWidth, canvasHeight) * 0.84f
    };

    public ReferenceRenderOptions ToRenderOptions(Surface? texture) => new()
    {
        YawDegrees = YawDegrees,
        PitchDegrees = PitchDegrees,
        RollDegrees = RollDegrees,
        Ambient = Ambient,
        Color = Color,
        Texture = texture,
        Placement = new ReferencePlacement(CenterX, CenterY, Diameter, MirrorX, MirrorY)
    };

    /// <summary>Settings after the canvas is cropped at (x, y) or padded by (-x, -y).</summary>
    public LiveModelSettings Shifted(float dx, float dy) => this with { CenterX = CenterX + dx, CenterY = CenterY + dy };

    /// <summary>Settings after the canvas is resampled by (sx, sy). A model cannot stretch, so a
    /// non-uniform resize scales it by the geometric mean.</summary>
    public LiveModelSettings Scaled(double sx, double sy) => this with
    {
        CenterX = (float)(CenterX * sx),
        CenterY = (float)(CenterY * sy),
        Diameter = (float)(Diameter * Math.Sqrt(sx * sy))
    };

    /// <summary>Settings after the canvas turns <paramref name="degrees"/> clockwise about its centre,
    /// from a <paramref name="oldWidth"/> x <paramref name="oldHeight"/> canvas to a
    /// <paramref name="newWidth"/> x <paramref name="newHeight"/> one.</summary>
    public LiveModelSettings Rotated(double degrees, int oldWidth, int oldHeight, int newWidth, int newHeight)
    {
        double radians = degrees * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        double cx = CenterX - oldWidth / 2.0, cy = CenterY - oldHeight / 2.0;
        // A mirrored model turns the other way round its own axis.
        float roll = (float)(MirrorX ^ MirrorY ? degrees : -degrees);
        return this with
        {
            CenterX = (float)(newWidth / 2.0 + cx * cos - cy * sin),
            CenterY = (float)(newHeight / 2.0 + cx * sin + cy * cos),
            RollDegrees = NormalizeDegrees(RollDegrees + roll)
        };
    }

    public LiveModelSettings FlippedHorizontally(int canvasWidth) =>
        this with { CenterX = canvasWidth - CenterX, MirrorX = !MirrorX };

    public LiveModelSettings FlippedVertically(int canvasHeight) =>
        this with { CenterY = canvasHeight - CenterY, MirrorY = !MirrorY };

    private static float NormalizeDegrees(float degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees <= -180) degrees += 360;
        return degrees;
    }
}

/// <summary>
/// What makes a layer a live 3D layer: the mesh it renders and how. Immutable, so history steps,
/// frame copies and document snapshots share it freely and swap whole values.
/// </summary>
public sealed record LiveModel(ObjMesh Mesh, string SourceName, LiveModelSettings Settings)
{
    public LiveModel WithSettings(LiveModelSettings settings) => this with { Settings = settings };
}

/// <summary>Renders live 3D layers and keeps their pixels in step with their settings.</summary>
public static class LiveModelRenderer
{
    /// <summary>Renders <paramref name="model"/> on a transparent canvas-sized surface.</summary>
    public static Surface Render(LiveModel model, int width, int height, Surface? texture) =>
        ReferenceRenderer.Render(model.Mesh, width, height, model.Settings.ToRenderOptions(texture));

    /// <summary>The surface a live layer's texture link points at, from the frame holding the
    /// layer. Null when there is no link, it is dangling, or it points at the layer itself.</summary>
    public static Surface? ResolveTexture(Document doc, Layer layer)
    {
        if (layer.LiveModel?.Settings.TextureLayerId is not { } id || id == layer.Id) return null;
        foreach (DocumentFrame frame in doc.Frames)
        {
            if (!frame.Layers.Contains(layer)) continue;
            foreach (Layer candidate in frame.Layers)
                if (candidate.Id == id && !ReferenceEquals(candidate, layer)) return candidate.Surface;
            return null;
        }
        return null;
    }

    /// <summary>Re-renders a live layer's pixels in place. No-op for a raster layer.</summary>
    public static void Refresh(Document doc, Layer layer)
    {
        if (layer.LiveModel is not { } model) return;
        using Surface rendered = Render(model, layer.Width, layer.Height, ResolveTexture(doc, layer));
        layer.Surface.CopyFrom(rendered);
    }

    /// <summary>Re-renders every live layer of every frame. Untextured layers render first, so a
    /// live layer used as another's texture is current before it is worn.</summary>
    public static void RefreshAll(Document doc)
    {
        foreach (DocumentFrame frame in doc.Frames)
        {
            foreach (Layer layer in frame.Layers)
                if (layer.LiveModel is { Settings.TextureLayerId: null }) Refresh(doc, layer);
            foreach (Layer layer in frame.Layers)
                if (layer.LiveModel is { Settings.TextureLayerId: not null }) Refresh(doc, layer);
        }
    }

    /// <summary>Cheap fingerprint of a surface, for noticing that a texture layer changed.</summary>
    public static unsafe int Fingerprint(Surface surface)
    {
        var hash = new HashCode();
        hash.Add(surface.Width);
        hash.Add(surface.Height);
        int rowBytes = surface.Width * sizeof(ColorBgra);
        for (int y = 0; y < surface.Height; y++)
            hash.AddBytes(new ReadOnlySpan<byte>((byte*)surface.GetRowPointer(y), rowBytes));
        return hash.ToHashCode();
    }
}

/// <summary>One undoable change to a live 3D layer: its model (settings or the model itself) and
/// the pixels rendered from it, swapped as a pair.</summary>
public sealed class LiveModelMemento : HistoryMemento
{
    private readonly Layer _layer;
    private readonly LiveModel? _model;
    private Surface? _pixels;

    /// <summary>Captures the layer's current model and pixels; push it, then change the layer.</summary>
    public LiveModelMemento(Layer layer, string name) : this(layer, layer.LiveModel, layer.Surface.Clone(), name) { }

    private LiveModelMemento(Layer layer, LiveModel? model, Surface pixels, string name) : base(name)
    {
        _layer = layer;
        _model = model;
        _pixels = pixels;
    }

    public override long ApproximateBytes => _pixels is null ? 0 : (long)_pixels.Stride * _pixels.Height;

    public override HistoryMemento Undo()
    {
        Surface saved = _pixels ?? throw new ObjectDisposedException(nameof(LiveModelMemento));
        var redo = new LiveModelMemento(_layer, _layer.LiveModel, _layer.Surface.Clone(), Name);
        _layer.Surface.CopyFrom(saved);
        _layer.LiveModel = _model;
        saved.Dispose();
        _pixels = null;
        return redo;
    }

    public override void Dispose()
    {
        _pixels?.Dispose();
        _pixels = null;
    }
}

/// <summary>Compact binary form of a render mesh, for embedding live 3D layers in .kwp files.</summary>
public static class MeshBinary
{
    private const uint Magic = 0x314D504B; // "KPM1"

    public static void Write(ObjMesh mesh, Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(mesh.Vertices.Count);
        writer.Write(mesh.Normals.Count);
        writer.Write(mesh.TexCoords.Count);
        writer.Write(mesh.Triangles.Count);
        foreach (Vector3 v in mesh.Vertices) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
        foreach (Vector3 n in mesh.Normals) { writer.Write(n.X); writer.Write(n.Y); writer.Write(n.Z); }
        foreach (Vector2 t in mesh.TexCoords) { writer.Write(t.X); writer.Write(t.Y); }
        foreach (ObjTriangle t in mesh.Triangles)
        {
            writer.Write(t.A); writer.Write(t.B); writer.Write(t.C);
            writer.Write(t.NA); writer.Write(t.NB); writer.Write(t.NC);
            writer.Write(t.TA); writer.Write(t.TB); writer.Write(t.TC);
        }
    }

    /// <summary>Reads a mesh written by <see cref="Write"/>, validating every count and index.</summary>
    public static ObjMesh Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        try
        {
            if (reader.ReadUInt32() != Magic) throw new InvalidDataException("not a KawaPaint mesh");
            int vertexCount = ReadCount(reader), normalCount = ReadCount(reader);
            int texCount = ReadCount(reader), triangleCount = ReadCount(reader);
            var vertices = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++) vertices[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var normals = new Vector3[normalCount];
            for (int i = 0; i < normalCount; i++) normals[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var texCoords = new Vector2[texCount];
            for (int i = 0; i < texCount; i++) texCoords[i] = new(reader.ReadSingle(), reader.ReadSingle());
            var triangles = new ObjTriangle[triangleCount];
            for (int i = 0; i < triangleCount; i++)
            {
                triangles[i] = new ObjTriangle(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
                    reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
                    reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            }
            return ObjMesh.Create(vertices, triangles, texCoords, normals);
        }
        catch (EndOfStreamException) { throw new InvalidDataException("truncated KawaPaint mesh"); }
        catch (ArgumentException ex) { throw new InvalidDataException("invalid KawaPaint mesh: " + ex.Message); }
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count is < 0 or > 30_000_000) throw new InvalidDataException("mesh element count is out of range");
        return count;
    }
}
