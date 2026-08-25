using System;
using System.Linq;
using Avalonia.Platform.Storage;
using KawaPaint.Engine;
using KawaPaint.Engine.ThreeD;

namespace KawaPaint.App;

public partial class MainView
{
    private static readonly FilePickerFileType ReferenceModelFileType = new("3D reference model")
    {
        Patterns = new[] { "*.obj", "*.gltf", "*.glb" },
        MimeTypes = new[] { "model/obj", "model/gltf+json", "model/gltf-binary", "text/plain" }
    };

    private async void OnImport3DReference(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Document? doc = Canvas.Document;
        if (doc is null) return;
        RecordSkipped("Import 3D Reference");

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a 3D reference model",
            AllowMultiple = false,
            FileTypeFilter = new[] { ReferenceModelFileType }
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        try
        {
            ReferenceRenderOptions options = new();
            if (OwnerWindow is { } owner)
            {
                var dialog = new Model3DImportDialog(file.Name);
                if (!await dialog.ShowDialog<bool>(owner)) return;
                options = dialog.Options;
            }

            await using var stream = await file.OpenReadAsync();
            string extension = System.IO.Path.GetExtension(file.Name);
            ReferenceModel model = extension.Equals(".obj", StringComparison.OrdinalIgnoreCase)
                ? ObjModelLoader.Load(stream)
                : GltfModelLoader.Load(stream, ExternalBufferResolver(file));
            using Surface rendered = ReferenceModelRenderer.Render(model, doc.Width, doc.Height, options);
            Layer layer = doc.AddLayer(System.IO.Path.GetFileNameWithoutExtension(file.Name) + " 3D reference");
            SurfaceOps.CompositeOver(layer.Surface, rendered, 0, 0);
            Canvas.SetActiveLayer(layer);
            Canvas.History.Push(new DelegateMemento("Import 3D Reference",
                undo: () => { doc.RemoveLayer(layer); Canvas.SetActiveLayer(doc.Layers[^1]); },
                redo: () => { doc.AddLayer(layer); Canvas.SetActiveLayer(layer); },
                approximateBytes: () => doc.IndexOf(layer) < 0 ? SurfaceBytes(layer.Surface) : 0,
                dispose: () => { if (doc.IndexOf(layer) < 0) layer.Dispose(); }));
            RefreshDocument();
            StatusText.Text = $"Rasterized {model.Vertices.Count:N0} vertices / {model.Triangles.Count:N0} triangles";
        }
        catch (Exception ex)
        {
            StatusText.Text = "3D import failed: " + ex.Message;
        }
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
