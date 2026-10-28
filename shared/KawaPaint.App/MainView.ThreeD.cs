using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using KawaPaint.Engine;
using KawaPaint.Engine.ThreeD;

namespace KawaPaint.App;

public partial class MainView
{
    private static readonly FilePickerFileType ReferenceModelFileType = new("3D model")
    {
        Patterns = new[] { "*.obj", "*.gltf", "*.glb" },
        MimeTypes = new[] { "model/obj", "model/gltf+json", "model/gltf-binary", "text/plain" }
    };

    private async void OnImport3DReference(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Document? doc = Canvas.Document;
        if (doc is null) return;
        RecordSkipped("Import 3D Model");

        try
        {
            if (await PickModelAsync("Import a 3D model") is not { } picked) return;
            var (mesh, fileName) = picked;
            if (!ReferenceEquals(doc, Canvas.Document)) return;

            var dialog = new ThreeDLayerDialog(mesh, fileName, doc.Width, doc.Height,
                LiveModelSettings.Centered(doc.Width, doc.Height), TextureChoicesFor(doc, null), importing: true);
            if (!await ShowThreeDLayerDialogAsync(dialog) || !ReferenceEquals(doc, Canvas.Document)) return;

            var model = new LiveModel(mesh, fileName, dialog.ResultSettings);
            StatusText.Text = $"Rendering {mesh.Triangles.Count:N0} triangles…";
            Surface? texture = dialog.ResultTexture?.Clone();
            Surface rendered;
            try { rendered = await Task.Run(() => LiveModelRenderer.Render(model, doc.Width, doc.Height, texture)); }
            finally { texture?.Dispose(); }
            if (!ReferenceEquals(doc, Canvas.Document))
            {
                rendered.Dispose();
                return;
            }

            bool live = dialog.KeepLive;
            var layer = new Layer(rendered, System.IO.Path.GetFileNameWithoutExtension(fileName) + (live ? " 3D" : " 3D reference"))
            {
                LiveModel = live ? model : null
            };
            try { doc.AddLayer(layer); }
            catch { layer.Dispose(); throw; }
            Canvas.SetActiveLayer(layer);
            Canvas.History.Push(new DelegateMemento("Import 3D Model",
                undo: () => { doc.RemoveLayer(layer); Canvas.SetActiveLayer(doc.Layers[^1]); },
                redo: () => { doc.AddLayer(layer); Canvas.SetActiveLayer(layer); },
                approximateBytes: applied => applied ? 0 : SurfaceBytes(layer.Surface),
                dispose: applied => { if (!applied) layer.Dispose(); }));
            RefreshDocument();
            StatusText.Text = live
                ? $"Live 3D layer: {mesh.Triangles.Count:N0} triangles (right-click the layer for its 3D properties)"
                : $"Rasterized {mesh.Vertices.Count:N0} vertices / {mesh.Triangles.Count:N0} triangles";
        }
        catch (Exception ex)
        {
            StatusText.Text = "3D import failed: " + ex.Message;
        }
    }

    /// <summary>Layers of the active frame a model can wear, excluding <paramref name="self"/>.</summary>
    private static List<TextureChoice> TextureChoicesFor(Document doc, Layer? self)
    {
        var choices = new List<TextureChoice>();
        for (int i = doc.LayerCount - 1; i >= 0; i--)
            if (!ReferenceEquals(doc.Layers[i], self))
                choices.Add(new TextureChoice(doc.Layers[i].Id, doc.Layers[i].Name, doc.Layers[i].Surface));
        return choices;
    }

    private async Task<bool> ShowThreeDLayerDialogAsync(ThreeDLayerDialog dialog)
    {
        if (OwnerWindow is { } owner) return await dialog.ShowDialog<bool>(owner);
        return await ShowCanvasWindowContentAsync(dialog, dialog.UseCanvasHost,
            dialog.CancelCanvasHost, dialog.BeginCanvasHost);
    }

    private async void OnLiveLayerProperties(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Document? doc = Canvas.Document;
        if (doc is null || Canvas.ActiveLayer is not { LiveModel: { } model } layer)
        {
            StatusText.Text = "The active layer is not a live 3D layer";
            return;
        }
        RecordSkipped("3D Layer Properties");

        var dialog = new ThreeDLayerDialog(model.Mesh, model.SourceName, doc.Width, doc.Height, model.Settings,
            TextureChoicesFor(doc, layer), importing: false);
        if (!await ShowThreeDLayerDialogAsync(dialog)) return;
        // The layer may have been rasterized, removed or the document swapped while the dialog was up.
        if (!ReferenceEquals(doc, Canvas.Document) || !doc.OwnsLayer(layer) || !ReferenceEquals(layer.LiveModel, model))
            return;
        LiveModelSettings settings = dialog.ResultSettings;
        if (settings == model.Settings) return;

        try
        {
            var memento = new LiveModelMemento(layer, "3D Layer Properties");
            layer.LiveModel = model.WithSettings(settings);
            try { LiveModelRenderer.Refresh(doc, layer); }
            catch
            {
                memento.Undo().Dispose();
                memento.Dispose();
                throw;
            }
            Canvas.History.Push(memento);
            RememberLiveRender(doc, layer);
            RefreshDocument();
            StatusText.Text = "3D layer updated";
        }
        catch (Exception ex)
        {
            StatusText.Text = "3D render failed: " + ex.Message;
        }
    }

    private void OnRasterizeLiveLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas.ActiveLayer is not { LiveModel: { } model } layer) return;
        RecordSkipped("Rasterize 3D Layer");
        layer.LiveModel = null;
        Canvas.History.Push(new DelegateMemento("Rasterize 3D Layer",
            undo: () => layer.LiveModel = model,
            redo: () => layer.LiveModel = null));
        RefreshDocument();
        StatusText.Text = $"“{layer.Name}” is now plain pixels";
    }

    // What each live layer was last rendered from: its model and its texture's fingerprint. A
    // texture layer changes through ordinary edits, so after every history change the textured
    // live layers of the active frame compare and re-render when their texture moved on.
    private readonly ConditionalWeakTable<Layer, Tuple<LiveModel, int>> _liveRenders = new();
    private int _liveRefreshRevision;
    private Layer? _liveWatchLayer;

    private void RememberLiveRender(Document doc, Layer layer)
    {
        if (layer.LiveModel is not { } model) return;
        Surface? texture = LiveModelRenderer.ResolveTexture(doc, layer);
        _liveRenders.AddOrUpdate(layer, Tuple.Create(model, texture is null ? 0 : LiveModelRenderer.Fingerprint(texture)));
    }

    /// <summary>Runs after every history change: re-renders live layers whose texture changed and
    /// says so when an edit just turned a live layer into plain pixels.</summary>
    private async void OnHistoryChangedForLiveLayers()
    {
        Document? doc = Canvas.Document;
        if (doc is null) return;

        Layer? active = Canvas.ActiveLayer;
        if (_liveWatchLayer is { } watched && ReferenceEquals(watched, active) && watched.LiveModel is null &&
            _liveRenders.TryGetValue(watched, out _) && Canvas.History.NextUndoName is { } step &&
            step != "Rasterize 3D Layer")
            StatusText.Text = $"“{watched.Name}” is now plain pixels — Undo to keep it as a live 3D layer";
        _liveWatchLayer = active?.LiveModel is not null ? active : null;
        if (active?.LiveModel is not null && !_liveRenders.TryGetValue(active, out _))
            RememberLiveRender(doc, active);

        var stale = new List<(Layer Layer, LiveModel Model, Surface Texture, int Fingerprint)>();
        foreach (Layer layer in doc.Layers)
        {
            if (layer.LiveModel is not { Settings.TextureLayerId: not null } model) continue;
            if (LiveModelRenderer.ResolveTexture(doc, layer) is not { } texture) continue;
            int fingerprint = LiveModelRenderer.Fingerprint(texture);
            if (_liveRenders.TryGetValue(layer, out var last) && ReferenceEquals(last.Item1, model) &&
                last.Item2 == fingerprint)
                continue;
            stale.Add((layer, model, texture.Clone(), fingerprint));
        }
        if (stale.Count == 0) return;

        int revision = ++_liveRefreshRevision;
        try
        {
            var rendered = await Task.Run(() => stale.Select(item =>
                LiveModelRenderer.Render(item.Model, item.Layer.Width, item.Layer.Height, item.Texture)).ToList());
            bool current = revision == _liveRefreshRevision && ReferenceEquals(doc, Canvas.Document);
            for (int i = 0; i < stale.Count; i++)
            {
                var item = stale[i];
                if (current && doc.OwnsLayer(item.Layer) && ReferenceEquals(item.Layer.LiveModel, item.Model))
                {
                    item.Layer.Surface.CopyFrom(rendered[i]);
                    _liveRenders.AddOrUpdate(item.Layer, Tuple.Create(item.Model, item.Fingerprint));
                }
                rendered[i].Dispose();
            }
            if (current) RefreshDocument();
        }
        catch (Exception ex)
        {
            StatusText.Text = "3D render failed: " + ex.Message;
        }
        finally
        {
            foreach (var item in stale) item.Texture.Dispose();
        }
    }

    /// <summary>Asks for an OBJ/glTF/GLB file and loads it off the UI thread. Null when the user
    /// cancels; load errors propagate to the caller.</summary>
    private async Task<(ObjMesh Mesh, string FileName)?> PickModelAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { ReferenceModelFileType }
        });
        var file = files.FirstOrDefault();
        if (file is null) return null;

        byte[] bytes;
        await using (Stream source = await file.OpenReadAsync())
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }

        string extension = System.IO.Path.GetExtension(file.Name);
        var resolver = ExternalBufferResolver(file);
        ObjMesh mesh = await Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return extension.Equals(".obj", StringComparison.OrdinalIgnoreCase)
                ? ObjMesh.Load(stream)
                : GltfModelLoader.Load(stream, resolver).ToRenderMesh();
        });
        return (mesh, file.Name);
    }

    private static Func<string, byte[]?>? ExternalBufferResolver(IStorageFile modelFile)
    {
        if (!modelFile.Path.IsAbsoluteUri || !modelFile.Path.IsFile) return null;
        string? directory = System.IO.Path.GetDirectoryName(modelFile.Path.LocalPath);
        if (directory is null) return null;
        string root = System.IO.Path.GetFullPath(directory) + System.IO.Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return relative =>
        {
            if (Uri.TryCreate(relative, UriKind.Absolute, out _)) return null;
            string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(root,
                relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root, comparison) || !System.IO.File.Exists(candidate)) return null;
            var info = new System.IO.FileInfo(candidate);
            if (info.Length > 512L * 1024 * 1024) return null;
            return System.IO.File.ReadAllBytes(candidate);
        };
    }
}
