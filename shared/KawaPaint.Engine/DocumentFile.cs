// KawaPaint — native layered document format (.kwp). A ZIP archive holding a JSON manifest
// plus one lossless PNG per layer (layers/0.png = bottom). Portable and inspectable, and it
// preserves layer names, opacity, visibility, and blend mode.

using System.IO.Compression;
using System.Text.Json;

namespace KawaPaint.Engine;

public static class DocumentFile
{
    public const string Extension = ".kwp";
    private const int FormatVersion = 1;

    private sealed class Manifest
    {
        public int Version { get; set; } = FormatVersion;
        public int Width { get; set; }
        public int Height { get; set; }
        public List<LayerInfo> Layers { get; set; } = new();
    }

    private sealed class LayerInfo
    {
        public string Name { get; set; } = "Layer";
        public byte Opacity { get; set; } = 255;
        public bool Visible { get; set; } = true;
        public string BlendMode { get; set; } = nameof(KawaPaint.Engine.BlendMode.Normal);
    }

    public static void Save(Document doc, string path)
    {
        using var file = File.Create(path);
        Save(doc, file);
    }

    public static void Save(Document doc, Stream stream)
    {
        var manifest = new Manifest { Width = doc.Width, Height = doc.Height };
        foreach (var layer in doc.Layers)
        {
            manifest.Layers.Add(new LayerInfo
            {
                Name = layer.Name,
                Opacity = layer.Opacity,
                Visible = layer.Visible,
                BlendMode = layer.BlendMode.ToString()
            });
        }

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var ms = manifestEntry.Open())
            JsonSerializer.Serialize(ms, manifest, new JsonSerializerOptions { WriteIndented = true });

        for (int i = 0; i < doc.LayerCount; i++)
        {
            var entry = zip.CreateEntry($"layers/{i}.png", CompressionLevel.Fastest); // PNG is already compressed
            using var es = entry.Open();
            doc.Layers[i].Surface.Encode(es);
        }
    }

    public static Document Load(string path)
    {
        using var file = File.OpenRead(path);
        return Load(file);
    }

    public static Document Load(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException("not a KawaPaint document (missing manifest.json)");

        Manifest manifest;
        using (var ms = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(ms)
                ?? throw new InvalidDataException("corrupt manifest.json");

        var doc = new Document(manifest.Width, manifest.Height);
        for (int i = 0; i < manifest.Layers.Count; i++)
        {
            var info = manifest.Layers[i];
            var entry = zip.GetEntry($"layers/{i}.png")
                ?? throw new InvalidDataException($"missing layer image layers/{i}.png");

            Surface surface;
            using (var es = entry.Open())
            using (var buffer = new MemoryStream())
            {
                es.CopyTo(buffer);          // decode needs a seekable stream
                buffer.Position = 0;
                surface = Surface.Decode(buffer);
            }

            var layer = new Layer(surface, info.Name)
            {
                Opacity = info.Opacity,
                Visible = info.Visible,
                BlendMode = Enum.TryParse<BlendMode>(info.BlendMode, out var bm) ? bm : BlendMode.Normal
            };
            doc.AddLayer(layer);
        }

        return doc;
    }
}
