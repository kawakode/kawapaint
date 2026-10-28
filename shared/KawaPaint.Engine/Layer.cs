using KawaPaint.Engine.ThreeD;

namespace KawaPaint.Engine;

/// <summary>A single bitmap layer: a Surface plus compositing properties.</summary>
public sealed class Layer : IDisposable
{
    /// <summary>Stable identity, saved in .kwp, so a live 3D layer can name the layer it wears as
    /// its texture. Snapshots (frame copies, document clones, geometry ops) keep it; a user-facing
    /// Duplicate Layer gives the copy a fresh one.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; }
    public byte Opacity { get; set; } = 255;
    public bool Visible { get; set; } = true;
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;
    public Surface Surface { get; }

    /// <summary>The model this layer is a live render of, or null for an ordinary raster layer.
    /// When set, the pixels are a cache of <see cref="LiveModelRenderer.Render"/>; any pixel edit
    /// recorded by a TileDeltaMemento or LayerSurfaceMemento drops it, and undoing the edit brings it back.</summary>
    public LiveModel? LiveModel { get; set; }

    public int Width => Surface.Width;
    public int Height => Surface.Height;

    public Layer(int width, int height, string? name = null)
    {
        Surface = new Surface(width, height);
        Name = name ?? "Layer";
    }

    public Layer(Surface surface, string? name = null)
    {
        Surface = surface;
        Name = name ?? "Layer";
    }

    /// <summary>Deep copy (pixels + properties).</summary>
    public Layer Clone() => new(Surface.Clone(), Name + " copy")
    {
        Id = Id,
        LiveModel = LiveModel,
        Opacity = Opacity,
        Visible = Visible,
        BlendMode = BlendMode
    };

    public void Dispose() => Surface.Dispose();
}
