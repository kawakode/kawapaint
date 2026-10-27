// KawaPaint - painting on a 3D model. The document is the model's texture: the 3D Paint panel
// shows the model wearing the canvas composite and updates as the image changes, and in paint
// mode a drag over the model paints the active layer at the texture coordinates under the pointer.
// The model itself is view state, like a reference image: it is not saved in the document.

using System;
using System.Numerics;
using KawaPaint.App.Core;
using KawaPaint.Engine;
using KawaPaint.Engine.ThreeD;

namespace KawaPaint.App;

public partial class MainView
{
    private Layer? _paint3DLayer;
    private Surface? _paint3DSnapshot;
    private Vector2? _lastDabTexel;

    private void SetupTexturePaint()
    {
        Paint3DView.TextureSource = () => Canvas.Composite;
        Paint3DView.PaintStarted += BeginModelPaint;
        Paint3DView.PaintSample += PaintModelAt;
        Paint3DView.PaintEnded += EndModelPaint;
        Canvas.CompositeChanged += () =>
        {
            if (Paint3DView.Mesh is not null && _panels.IsVisible("Paint3D")) Paint3DView.RequestRender();
        };
        UpdatePaint3DControls();
    }

    private async void OnOpenPaintModel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas.Document is null) return;
        RecordSkipped("Paint on 3D Model");
        try
        {
            if (await PickModelAsync("Open a model to paint on") is not { } picked) return;
            ShowPaintModel(picked.Mesh, picked.FileName);
        }
        catch (Exception ex)
        {
            StatusText.Text = "3D model failed to open: " + ex.Message;
        }
    }

    /// <summary>Puts a model in the 3D Paint panel and shows the panel.</summary>
    internal void ShowPaintModel(ObjMesh mesh, string name)
    {
        Paint3DView.Mesh = mesh;
        if (!_panels.IsVisible("Paint3D")) _panels.SetPlace("Paint3D", PanelPlace.Floating);
        Paint3DPaintToggle.IsChecked = mesh.HasTexCoords;
        Paint3DView.PaintMode = mesh.HasTexCoords;
        UpdatePaint3DControls();
        Paint3DStatus.Text = mesh.HasTexCoords
            ? $"{name}: {mesh.Triangles.Count:N0} triangles. The whole image is its texture - paint here or on the canvas."
            : $"{name} has no texture coordinates (UVs), so it can be turned and viewed but not painted.";
        StatusText.Text = "3D Paint: " + name;
    }

    private void UpdatePaint3DControls()
    {
        bool mapped = Paint3DView.Mesh is { HasTexCoords: true };
        Paint3DPaintToggle.IsEnabled = mapped;
        Paint3DUvLayoutBtn.IsEnabled = mapped;
    }

    private void OnTogglePaintOnModel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Paint3DView.PaintMode = Paint3DPaintToggle.IsChecked == true;

    private void OnResetPaintModelView(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Paint3DView.ResetView();

    private void OnAddUvLayoutLayer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas.Document is not { } doc || Paint3DView.Mesh is not { HasTexCoords: true } mesh) return;
        var layer = new Layer(TexturePainter.RenderUvLayout(mesh, doc.Width, doc.Height,
            ColorBgra.FromBgra(255, 160, 0, 220)), "UV Layout");
        try { doc.AddLayer(layer); }
        catch { layer.Dispose(); throw; }
        Canvas.SetActiveLayer(layer);
        Canvas.History.Push(new DelegateMemento("Add UV Layout",
            undo: () => { doc.RemoveLayer(layer); Canvas.SetActiveLayer(doc.Layers[^1]); },
            redo: () => { doc.AddLayer(layer); Canvas.SetActiveLayer(layer); },
            approximateBytes: () => !doc.OwnsLayer(layer) ? SurfaceBytes(layer.Surface) : 0,
            dispose: () => { if (!doc.OwnsLayer(layer)) layer.Dispose(); }));
        RefreshDocument();
        StatusText.Text = "Added the UV layout as a layer - hide it before exporting the texture";
    }

    private void BeginModelPaint()
    {
        EndModelPaint();
        if (Canvas.ActiveLayer is not { } layer) return;
        _paint3DLayer = layer;
        _paint3DSnapshot = layer.Surface.Clone();
        _lastDabTexel = null;
    }

    /// <summary>One sample of a paint drag on the model: dabs the brush at that texture point,
    /// spaced so a slow drag doesn't pile dabs on one spot.</summary>
    private void PaintModelAt(Vector2 uv)
    {
        if (_paint3DLayer is not { } layer || _paint3DSnapshot is not { } snapshot) return;
        Surface surface = layer.Surface;
        float radius = Math.Max(0.5f, Canvas.BrushWidth / 2f);
        var texel = new Vector2((uv.X - MathF.Floor(uv.X)) * surface.Width, (uv.Y - MathF.Floor(uv.Y)) * surface.Height);
        if (_lastDabTexel is { } last && Vector2.Distance(last, texel) < Math.Max(1f, radius * 0.35f)) return;
        _lastDabTexel = texel;

        foreach (var (x, y, w, h) in TexturePainter.Dab(surface, uv, radius, Canvas.BrushColor))
        {
            if (Canvas.Selection is { IsActive: true } selection) selection.Clip(surface, snapshot, x, y, w, h);
            Canvas.RenderComposite(x, y, w, h);
        }
        Canvas.InvalidateVisual();
    }

    private void EndModelPaint()
    {
        if (_paint3DLayer is not { } layer || _paint3DSnapshot is not { } snapshot) return;
        _paint3DLayer = null;
        _paint3DSnapshot = null;
        if (Canvas.Document?.OwnsLayer(layer) == true)
            Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, "3D Paint"));   // disposes the snapshot
        else
            snapshot.Dispose();
        Canvas.NotifyLayersChanged();
    }
}
