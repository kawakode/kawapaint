namespace KawaPaint.Engine;

using KawaPaint.Engine.MailMerge;

/// <summary>
/// An image: a fixed canvas size and an ordered stack of layers (index 0 = bottom).
/// Compositing walks bottom → top using each layer's blend mode + opacity.
/// </summary>
public sealed class Document : IDisposable
{
    private List<Layer> _layers;
    private readonly List<DocumentFrame> _frames = new();
    private readonly List<DynamicTextZone> _dynamicTextZones = new();

    public int Width { get; }
    public int Height { get; }

    public IReadOnlyList<Layer> Layers => _layers;
    public int LayerCount => _layers.Count;
    public IReadOnlyList<DocumentFrame> Frames => _frames;
    public int FrameCount => _frames.Count;
    public int ActiveFrameIndex { get; private set; }
    public DocumentFrame ActiveFrame => _frames[ActiveFrameIndex];
    public IList<DynamicTextZone> DynamicTextZones => _dynamicTextZones;

    /// <summary>Pixels per inch, for the ruler and any future print-size math. Purely metadata -
    /// nothing here rescales pixels based on it.</summary>
    public double Dpi { get; set; } = 96;

    /// <summary>Source EXIF TIFF payload, retained across project saves and compatible exports.</summary>
    public byte[]? ExifTiff { get; set; }

    public Document(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException();
        Width = width;
        Height = height;
        _layers = new List<Layer>();
        _frames.Add(new DocumentFrame(_layers, "Frame 1", 100));
    }

    /// <summary>Adds an independent frame and makes it active. Clone-current is convenient for
    /// onion-skin style animation; false starts with one blank layer.</summary>
    public DocumentFrame AddFrame(string? name = null, int durationMs = 100, bool cloneCurrent = false)
    {
        var layers = cloneCurrent
            ? _layers.Select(layer =>
            {
                Layer copy = layer.Clone(); copy.Name = layer.Name; return copy;
            }).ToList()
            : new List<Layer>();
        if (!cloneCurrent) layers.Add(new Layer(Width, Height, "Layer 1"));
        var frame = new DocumentFrame(layers, name ?? $"Frame {_frames.Count + 1}", durationMs);
        _frames.Add(frame);
        SetActiveFrame(_frames.Count - 1);
        return frame;
    }

    /// <summary>Adds an already-built frame. Used by animation decoders and project loading.</summary>
    public void AddFrame(DocumentFrame frame, bool makeActive = true)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Layers.Any(layer => layer.Width != Width || layer.Height != Height))
            throw new ArgumentException("frame layer size does not match document", nameof(frame));
        _frames.Add(frame);
        if (makeActive) SetActiveFrame(_frames.Count - 1);
    }

    public void SetActiveFrame(int index)
    {
        if ((uint)index >= (uint)_frames.Count) throw new ArgumentOutOfRangeException(nameof(index));
        ActiveFrameIndex = index;
        _layers = _frames[index].MutableLayers;
    }

    public void RemoveFrameAt(int index)
    {
        if (_frames.Count == 1) throw new InvalidOperationException("A document must keep at least one frame.");
        DocumentFrame removed = _frames[index];
        _frames.RemoveAt(index);
        if (ActiveFrameIndex >= _frames.Count) ActiveFrameIndex = _frames.Count - 1;
        else if (index < ActiveFrameIndex) ActiveFrameIndex--;
        _layers = _frames[ActiveFrameIndex].MutableLayers;
        removed.Dispose();
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
        => RenderTo(dest, 0, 0, Width, Height);

    /// <summary>Recomposites only a clipped canvas rectangle, preserving the rest of dest.</summary>
    public unsafe void RenderTo(Surface dest, int x, int y, int width, int height)
    {
        if (dest.Width != Width || dest.Height != Height)
            throw new ArgumentException("destination size does not match document");

        int left = Math.Clamp(x, 0, Width);
        int top = Math.Clamp(y, 0, Height);
        int right = (int)Math.Clamp((long)x + width, 0, Width);
        int bottom = (int)Math.Clamp((long)y + height, 0, Height);
        if (right <= left || bottom <= top) return;

        int count = right - left;
        dest.ClearRect(left, top, count, bottom - top, ColorBgra.Transparent);
        bool hasComposite = false;

        foreach (var layer in _layers)
        {
            if (!layer.Visible || layer.Opacity == 0) continue;

            var src = layer.Surface;
            byte op = layer.Opacity;
            BlendMode mode = layer.BlendMode;

            // Any source-over a fully transparent destination is exactly the source. Copying the
            // first ordinary layer also avoids a blend call and three floating-point channels for
            // every opaque pixel in the most common document shape.
            if (!hasComposite && mode == BlendMode.Normal && op == 255)
            {
                dest.CopyRectFrom(src, left, top, count, bottom - top);
                hasComposite = true;
                continue;
            }

            System.Threading.Tasks.Parallel.For(top, bottom, row =>
            {
                ColorBgra* dRow = (ColorBgra*)dest.GetRowPointer(row) + left;
                ColorBgra* sRow = (ColorBgra*)src.GetRowPointer(row) + left;
                Blending.CompositeSpan(mode, dRow, sRow, count, op);
            });
            hasComposite = true;
        }
    }

    public Surface Flatten()
    {
        var result = new Surface(Width, Height);
        RenderTo(result);
        return result;
    }

    /// <summary>Deep copy: every layer cloned (pixels + properties, exact name), same order, same
    /// Dpi. Used where a consistent snapshot is needed decoupled from further edits to the live
    /// document - e.g. autosave encoding on a background thread while the user keeps painting.
    /// Unlike <see cref="Layer.Clone"/> alone, names are copied exactly rather than getting its
    /// " copy" suffix - that suffix is right for a user-facing Duplicate Layer, wrong for a
    /// snapshot whose layer names get written straight into a saved file's manifest.</summary>
    public Document Clone()
    {
        var copy = new Document(Width, Height) { Dpi = Dpi, ExifTiff = ExifTiff?.ToArray() };
        copy.ActiveFrame.Dispose();
        copy._frames.Clear();
        foreach (DocumentFrame sourceFrame in _frames)
        {
            var layers = new List<Layer>(sourceFrame.Layers.Count);
            foreach (Layer layer in sourceFrame.Layers)
            {
                var cloned = layer.Clone();
                cloned.Name = layer.Name;
                layers.Add(cloned);
            }
            copy._frames.Add(new DocumentFrame(layers, sourceFrame.Name, sourceFrame.DurationMs));
        }
        copy.SetActiveFrame(ActiveFrameIndex);
        foreach (var zone in _dynamicTextZones) copy.DynamicTextZones.Add(zone.Clone());
        return copy;
    }

    public void Dispose()
    {
        foreach (DocumentFrame frame in _frames) frame.Dispose();
        _frames.Clear();
        _layers = new List<Layer>();
    }
}

/// <summary>One animation frame: an independent ordered layer stack plus display timing.</summary>
public sealed class DocumentFrame : IDisposable
{
    internal List<Layer> MutableLayers { get; }
    public IReadOnlyList<Layer> Layers => MutableLayers;
    public string Name { get; set; }
    public int DurationMs { get; set; }

    internal DocumentFrame(List<Layer> layers, string name, int durationMs)
    {
        MutableLayers = layers;
        Name = name;
        DurationMs = Math.Clamp(durationMs, 10, 600_000);
    }

    public DocumentFrame(IEnumerable<Layer> layers, string? name = null, int durationMs = 100)
        : this(layers.ToList(), name ?? "Frame", durationMs) { }

    public void Dispose()
    {
        foreach (Layer layer in MutableLayers) layer.Dispose();
        MutableLayers.Clear();
    }
}
