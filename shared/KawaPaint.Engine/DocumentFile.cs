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
    private const int FormatVersion = 3;

    private sealed class Manifest
    {
        public int Version { get; set; } = FormatVersion;
        public int Width { get; set; }
        public int Height { get; set; }
        public double Dpi { get; set; } = 96;
        public int ActiveFrame { get; set; }
        public byte[]? ExifTiff { get; set; }
        // Versions 1-2 stored a single layer stack here. Keep it for backwards loading.
        public List<LayerInfo> Layers { get; set; } = new();
        public List<FrameInfo> Frames { get; set; } = new();
        public List<DynamicTextZone> DynamicTextZones { get; set; } = new();
    }

    private sealed class FrameInfo
    {
        public string Name { get; set; } = "Frame";
        public int DurationMs { get; set; } = 100;
        public List<LayerInfo> Layers { get; set; } = new();
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
    public static void Save(Document doc, string path, CancellationToken cancellationToken = default)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } d ? d : ".";
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = File.Create(temp))
                Save(doc, file, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    public static void Save(Document doc, Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = CreateManifest(doc);
        var encodedLayers = doc.Frames.Select(frame => new byte[frame.Layers.Count][]).ToArray();

        // PNG encoding is CPU-heavy and independent per layer. Finish every encode before writing
        // the ZIP so a failing worker cannot leave a half-populated archive on the caller's stream.
        var coordinates = doc.Frames
            .SelectMany((frame, frameIndex) => frame.Layers.Select((_, layerIndex) => (frameIndex, layerIndex)))
            .ToArray();
        Parallel.ForEach(coordinates, new ParallelOptions { CancellationToken = cancellationToken }, coordinate =>
        {
            using var buffer = new MemoryStream();
            doc.Frames[coordinate.frameIndex].Layers[coordinate.layerIndex].Surface.Encode(buffer);
            encodedLayers[coordinate.frameIndex][coordinate.layerIndex] = buffer.ToArray();
        });

        cancellationToken.ThrowIfCancellationRequested();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var ms = manifestEntry.Open())
            JsonSerializer.Serialize(ms, manifest, new JsonSerializerOptions { WriteIndented = true });

        for (int frameIndex = 0; frameIndex < doc.FrameCount; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int layerIndex = 0; layerIndex < doc.Frames[frameIndex].Layers.Count; layerIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry($"frames/{frameIndex}/layers/{layerIndex}.png", CompressionLevel.Fastest);
                using var es = entry.Open();
                es.Write(encodedLayers[frameIndex][layerIndex]);
            }
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
        string framesDir = Path.Combine(directoryPath, "frames");
        Directory.CreateDirectory(framesDir);
        var tempPaths = new List<(string Temp, string Destination)>();
        try
        {
            for (int frameIndex = 0; frameIndex < doc.FrameCount; frameIndex++)
            {
                string layersDir = Path.Combine(framesDir, frameIndex.ToString(), "layers");
                Directory.CreateDirectory(layersDir);
                for (int layerIndex = 0; layerIndex < doc.Frames[frameIndex].Layers.Count; layerIndex++)
                {
                    string destination = Path.Combine(layersDir, $"{layerIndex}.png");
                    string temp = destination + $".{Guid.NewGuid():N}.tmp";
                    tempPaths.Add((temp, destination));
                    using var fs = File.Create(temp);
                    doc.Frames[frameIndex].Layers[layerIndex].Surface.Encode(fs);
                }
            }

            foreach (var path in tempPaths)
                File.Move(path.Temp, path.Destination, overwrite: true);

            // Remove only numeric PNGs that no longer belong to the declared frame/layer set.
            foreach (string existing in Directory.EnumerateFiles(framesDir, "*.png", SearchOption.AllDirectories))
            {
                string? layerDirectory = Path.GetDirectoryName(existing);
                string? frameDirectory = layerDirectory is null ? null : Path.GetDirectoryName(layerDirectory);
                int frameIndex = -1, layerIndex = -1;
                bool numeric = int.TryParse(Path.GetFileName(frameDirectory), out frameIndex)
                    && int.TryParse(Path.GetFileNameWithoutExtension(existing), out layerIndex);
                bool retained = numeric && frameIndex < doc.FrameCount
                    && layerIndex < doc.Frames[frameIndex].Layers.Count;
                if (numeric && !retained)
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
            foreach (var path in tempPaths)
            {
                try { if (File.Exists(path.Temp)) File.Delete(path.Temp); } catch { /* best-effort cleanup */ }
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

        var doc = new Document(manifest.Width, manifest.Height) { Dpi = manifest.Dpi, ExifTiff = manifest.ExifTiff };
        if (manifest.Version < 3)
        {
            for (int i = 0; i < manifest.Layers.Count; i++)
            {
                string relative = Path.Combine("layers", $"{i}.png");
                doc.AddLayer(LoadLayer(Path.Combine(directoryPath, relative), relative, manifest.Layers[i]));
            }
        }
        else
        {
            LoadFrames(doc, manifest, (frameIndex, layerIndex, info) =>
            {
                string relative = Path.Combine("frames", frameIndex.ToString(), "layers", $"{layerIndex}.png");
                return LoadLayer(Path.Combine(directoryPath, relative), relative, info);
            });
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

        var doc = new Document(manifest.Width, manifest.Height) { Dpi = manifest.Dpi, ExifTiff = manifest.ExifTiff };
        if (manifest.Version < 3)
        {
            for (int i = 0; i < manifest.Layers.Count; i++)
            {
                string relative = $"layers/{i}.png";
                doc.AddLayer(LoadLayer(zip, relative, manifest.Layers[i]));
            }
        }
        else
        {
            LoadFrames(doc, manifest, (frameIndex, layerIndex, info) =>
            {
                string relative = $"frames/{frameIndex}/layers/{layerIndex}.png";
                return LoadLayer(zip, relative, info);
            });
        }
        foreach (var zone in manifest.DynamicTextZones) doc.DynamicTextZones.Add(zone);

        return doc;
    }

    private static Manifest CreateManifest(Document doc)
    {
        var manifest = new Manifest
        {
            Width = doc.Width,
            Height = doc.Height,
            Dpi = doc.Dpi,
            ExifTiff = doc.ExifTiff,
            ActiveFrame = doc.ActiveFrameIndex
        };
        foreach (DocumentFrame frame in doc.Frames)
        {
            var frameInfo = new FrameInfo { Name = frame.Name, DurationMs = frame.DurationMs };
            foreach (Layer layer in frame.Layers)
            {
                frameInfo.Layers.Add(new LayerInfo
                {
                    Name = layer.Name,
                    Opacity = layer.Opacity,
                    Visible = layer.Visible,
                    BlendMode = layer.BlendMode.ToString()
                });
            }
            manifest.Frames.Add(frameInfo);
        }
        foreach (var zone in doc.DynamicTextZones) manifest.DynamicTextZones.Add(zone.Clone());
        return manifest;
    }

    private static void LoadFrames(Document doc, Manifest manifest,
        Func<int, int, LayerInfo, Layer> loadLayer)
    {
        if (manifest.Frames.Count == 0)
            throw new InvalidDataException("KawaPaint project contains no animation frames.");

        for (int frameIndex = 0; frameIndex < manifest.Frames.Count; frameIndex++)
        {
            FrameInfo info = manifest.Frames[frameIndex];
            var layers = info.Layers.Select((layer, layerIndex) => loadLayer(frameIndex, layerIndex, layer)).ToList();
            if (frameIndex == 0)
            {
                foreach (Layer layer in layers) doc.AddLayer(layer);
                doc.ActiveFrame.Name = info.Name;
                doc.ActiveFrame.DurationMs = info.DurationMs;
            }
            else
            {
                doc.AddFrame(new DocumentFrame(layers, info.Name, info.DurationMs), makeActive: false);
            }
        }
        doc.SetActiveFrame(Math.Clamp(manifest.ActiveFrame, 0, doc.FrameCount - 1));
    }

    private static Layer LoadLayer(string path, string displayPath, LayerInfo info)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"missing layer image {displayPath}");
        using var stream = File.OpenRead(path);
        return CreateLayer(Surface.Decode(stream), info);
    }

    private static Layer LoadLayer(ZipArchive zip, string path, LayerInfo info)
    {
        ZipArchiveEntry entry = zip.GetEntry(path)
            ?? throw new InvalidDataException($"missing layer image {path}");
        using var source = entry.Open();
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        buffer.Position = 0;
        return CreateLayer(Surface.Decode(buffer), info);
    }

    private static Layer CreateLayer(Surface surface, LayerInfo info) => new(surface, info.Name)
    {
        Opacity = info.Opacity,
        Visible = info.Visible,
        BlendMode = Enum.TryParse<BlendMode>(info.BlendMode, out BlendMode mode) ? mode : BlendMode.Normal
    };

    private static void ValidateVersion(int version)
    {
        if (version is < 1 or > FormatVersion)
            throw new InvalidDataException($"KawaPaint project format {version} is not supported by this build.");
    }
}
