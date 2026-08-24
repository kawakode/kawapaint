// KawaPaint - native layered document format (.kwp). A ZIP archive holding a JSON manifest
// plus one lossless PNG per layer (layers/0.png = bottom). Portable and inspectable, and it
// preserves layer names, opacity, visibility, and blend mode.

using System.IO.Compression;
using System.Text.Json;
using KawaPaint.Engine.MailMerge;

namespace KawaPaint.Engine;

public static class DocumentFile
{
    public const string Extension = ".kwp";
    private const int FormatVersion = 2;

    private sealed class Manifest
    {
        public int Version { get; set; } = FormatVersion;
        public int Width { get; set; }
        public int Height { get; set; }
        public double Dpi { get; set; } = 96;
        public List<LayerInfo> Layers { get; set; } = new();
        public List<DynamicTextZone> DynamicTextZones { get; set; } = new();
    }

    private sealed class LayerInfo
    {
        public string Name { get; set; } = "Layer";
        public byte Opacity { get; set; } = 255;
        public bool Visible { get; set; } = true;
        public string BlendMode { get; set; } = nameof(KawaPaint.Engine.BlendMode.Normal);
    }

    /// <summary>
    /// Writes to a temp file beside <paramref name="path"/> and only replaces it once the encode
    /// fully succeeds, so a failure mid-save (disk full, a layer surface going bad) never leaves a
    /// truncated file where a good one used to be.
    /// </summary>
    public static void Save(Document doc, string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } d ? d : ".";
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = File.Create(temp))
                Save(doc, file);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    public static void Save(Document doc, Stream stream)
    {
        var manifest = CreateManifest(doc);

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

    /// <summary>
    /// Directory form of the same format: <paramref name="directoryPath"/>/manifest.json plus
    /// <paramref name="directoryPath"/>/layers/N.png, uncompressed by any outer archive. Unlike
    /// <see cref="Save(Document,string)"/>'s single opaque zip, a git commit here only touches the
    /// files that actually changed - that's the whole point of this form existing.
    /// </summary>
    /// <summary>
    /// Encodes every layer to a temp file before touching anything real, so a failure partway
    /// through (disk full at layer 3 of 5) leaves the directory exactly as it was rather than a mix
    /// of new and stale/missing layer files. Only once every layer has encoded successfully are the
    /// temp files moved onto their real names, stale layer files from a since-shrunk layer count
    /// removed, and the manifest written last - it's what <see cref="LoadExploded"/> trusts, so it
    /// should never describe a layer set that isn't fully on disk yet.
    /// </summary>
    public static void SaveExploded(Document doc, string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        string layersDir = Path.Combine(directoryPath, "layers");
        Directory.CreateDirectory(layersDir);

        var tempPaths = new string?[doc.LayerCount];
        try
        {
            for (int i = 0; i < doc.LayerCount; i++)
            {
                tempPaths[i] = Path.Combine(layersDir, $"{i}.png.{Guid.NewGuid():N}.tmp");
                using var fs = File.Create(tempPaths[i]!);
                doc.Layers[i].Surface.Encode(fs);
            }

            for (int i = 0; i < doc.LayerCount; i++)
                File.Move(tempPaths[i]!, Path.Combine(layersDir, $"{i}.png"), overwrite: true);

            // Layer count can shrink between saves; stale files from a since-deleted layer must not
            // linger (LoadExploded would never see them, but they'd sit there confusing a git diff).
            // Safe to do only now, after the surviving layers' new content is committed to disk.
            foreach (string existing in Directory.EnumerateFiles(layersDir, "*.png"))
            {
                string name = Path.GetFileNameWithoutExtension(existing);
                if (int.TryParse(name, out int idx) && idx >= doc.LayerCount)
                    File.Delete(existing);
            }

            var manifest = CreateManifest(doc);

            string manifestPath = Path.Combine(directoryPath, "manifest.json");
            string manifestTemp = Path.Combine(directoryPath, $".manifest.json.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(manifestTemp, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(manifestTemp, manifestPath, overwrite: true);
        }
        finally
        {
            foreach (string? temp in tempPaths)
            {
                if (temp is null) break;   // nothing was assigned past the first unencoded slot
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort cleanup */ }
            }
        }
    }

    public static Document LoadExploded(string directoryPath)
    {
        string manifestPath = Path.Combine(directoryPath, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("not a KawaPaint project directory (missing manifest.json)");

        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("corrupt manifest.json");
        ValidateVersion(manifest.Version);

        var doc = new Document(manifest.Width, manifest.Height) { Dpi = manifest.Dpi };
        string layersDir = Path.Combine(directoryPath, "layers");
        for (int i = 0; i < manifest.Layers.Count; i++)
        {
            var info = manifest.Layers[i];
            string layerPath = Path.Combine(layersDir, $"{i}.png");
            if (!File.Exists(layerPath))
                throw new InvalidDataException($"missing layer image layers/{i}.png");

            Surface surface;
            using (var fs = File.OpenRead(layerPath))
                surface = Surface.Decode(fs);

            var layer = new Layer(surface, info.Name)
            {
                Opacity = info.Opacity,
                Visible = info.Visible,
                BlendMode = Enum.TryParse<BlendMode>(info.BlendMode, out var bm) ? bm : BlendMode.Normal
            };
            doc.AddLayer(layer);
        }
        foreach (var zone in manifest.DynamicTextZones) doc.DynamicTextZones.Add(zone);

        return doc;
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
        ValidateVersion(manifest.Version);

        var doc = new Document(manifest.Width, manifest.Height) { Dpi = manifest.Dpi };
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
        foreach (var zone in manifest.DynamicTextZones) doc.DynamicTextZones.Add(zone);

        return doc;
    }

    private static Manifest CreateManifest(Document doc)
    {
        var manifest = new Manifest { Width = doc.Width, Height = doc.Height, Dpi = doc.Dpi };
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
        foreach (var zone in doc.DynamicTextZones) manifest.DynamicTextZones.Add(zone.Clone());
        return manifest;
    }

    private static void ValidateVersion(int version)
    {
        if (version is < 1 or > FormatVersion)
            throw new InvalidDataException($"KawaPaint project format {version} is not supported by this build.");
    }
}
