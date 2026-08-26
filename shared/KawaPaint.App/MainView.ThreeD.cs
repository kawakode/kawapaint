using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
            byte[] bytes;
            await using (Stream source = await file.OpenReadAsync())
            {
                using var buffer = new MemoryStream();
                await source.CopyToAsync(buffer);
                bytes = buffer.ToArray();
            }

            string extension = System.IO.Path.GetExtension(file.Name);
            ObjMesh mesh = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return extension.Equals(".obj", StringComparison.OrdinalIgnoreCase)
                    ? ObjMesh.Load(stream)
                    : GltfModelLoader.Load(stream, ExternalBufferResolver(file)).ToRenderMesh();
            });

            ReferenceRenderOptions options = new();
            if (OwnerWindow is { } owner)
            {
                var dialog = new ThreeDImportDialog(mesh, file.Name);
                if (!await dialog.ShowDialog<bool>(owner)) return;
                options = dialog.ResultOptions;
            }

            StatusText.Text = $"Rendering {mesh.Triangles.Count:N0} triangles…";
            Surface rendered = await Task.Run(() => ReferenceRenderer.Render(mesh, doc.Width, doc.Height, options));
            if (!ReferenceEquals(doc, Canvas.Document))
            {
                rendered.Dispose();
                return;
            }

            var layer = new Layer(rendered,
                System.IO.Path.GetFileNameWithoutExtension(file.Name) + " 3D reference");
            try { doc.AddLayer(layer); }
            catch { layer.Dispose(); throw; }
            Canvas.SetActiveLayer(layer);
            Canvas.History.Push(new DelegateMemento("Import 3D Reference",
                undo: () => { doc.RemoveLayer(layer); Canvas.SetActiveLayer(doc.Layers[^1]); },
                redo: () => { doc.AddLayer(layer); Canvas.SetActiveLayer(layer); },
                approximateBytes: () => doc.IndexOf(layer) < 0 ? SurfaceBytes(layer.Surface) : 0,
                dispose: () => { if (doc.IndexOf(layer) < 0) layer.Dispose(); }));
            RefreshDocument();
            StatusText.Text = $"Rasterized {mesh.Vertices.Count:N0} vertices / {mesh.Triangles.Count:N0} triangles";
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
