namespace KawaPaint.Engine;

/// <summary>
/// An image: a fixed canvas size and an ordered stack of layers (index 0 = bottom).
/// Compositing walks bottom → top using each layer's blend mode + opacity.
/// </summary>
public sealed class Document : IDisposable
{
    private readonly List<Layer> _layers = new();

    public int Width { get; }
    public int Height { get; }

    public IReadOnlyList<Layer> Layers => _layers;
    public int LayerCount => _layers.Count;

    /// <summary>Pixels per inch, for the ruler and any future print-size math. Purely metadata —
    /// nothing here rescales pixels based on it.</summary>
    public double Dpi { get; set; } = 96;

    public Document(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException();
        Width = width;
        Height = height;
    }

    public Layer AddLayer(string? name = null)
    {
        var layer = new Layer(Width, Height, name ?? $"Layer {_layers.Count + 1}");
        _layers.Add(layer);
        return layer;
    }

    public void AddLayer(Layer layer)
    {
        if (layer.Width != Width || layer.Height != Height)
            throw new ArgumentException("layer size does not match document");
        _layers.Add(layer);
    }

    public void InsertLayer(int index, Layer layer)
    {
        if (layer.Width != Width || layer.Height != Height)
            throw new ArgumentException("layer size does not match document");
        _layers.Insert(index, layer);
    }

    public void RemoveLayer(Layer layer) => _layers.Remove(layer);
    public void RemoveLayerAt(int index) => _layers.RemoveAt(index);
    public int IndexOf(Layer layer) => _layers.IndexOf(layer);

    public void MoveLayer(int from, int to)
    {
        var layer = _layers[from];
        _layers.RemoveAt(from);
        _layers.Insert(to, layer);
    }

    /// <summary>Composites all visible layers into <paramref name="dest"/> (cleared to transparent first).</summary>
    public unsafe void RenderTo(Surface dest)
    {
        if (dest.Width != Width || dest.Height != Height)
            throw new ArgumentException("destination size does not match document");

        dest.Clear(ColorBgra.Transparent);

        foreach (var layer in _layers)
        {
            if (!layer.Visible || layer.Opacity == 0) continue;

            var src = layer.Surface;
            byte op = layer.Opacity;
            BlendMode mode = layer.BlendMode;

            int width = Width;
            System.Threading.Tasks.Parallel.For(0, Height, y =>
            {
                ColorBgra* dRow = (ColorBgra*)dest.GetRowPointer(y);
                ColorBgra* sRow = (ColorBgra*)src.GetRowPointer(y);
                for (int x = 0; x < width; x++)
                {
                    if (sRow[x].A == 0) continue;
                    dRow[x] = Blending.Composite(mode, dRow[x], sRow[x], op);
                }
            });
        }
    }

    public Surface Flatten()
    {
        var result = new Surface(Width, Height);
        RenderTo(result);
        return result;
    }

    public void Dispose()
    {
        foreach (var layer in _layers) layer.Dispose();
        _layers.Clear();
    }
}
