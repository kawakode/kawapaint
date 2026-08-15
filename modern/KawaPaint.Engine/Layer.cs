namespace KawaPaint.Engine;

/// <summary>A single bitmap layer: a Surface plus compositing properties.</summary>
public sealed class Layer : IDisposable
{
    public string Name { get; set; }
    public byte Opacity { get; set; } = 255;
    public bool Visible { get; set; } = true;
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;
    public Surface Surface { get; }

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

    public void Dispose() => Surface.Dispose();
}
