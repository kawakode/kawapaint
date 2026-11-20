// KawaPaint - Image > Stroke: draws a contour around the selection or, with nothing selected,
// around the opaque pixels of the active layer. Previewed live on the canvas while the dialog is
// open; OK commits one undo step, either on the active layer or as a new layer right above it.

using System;
using System.Threading.Tasks;
using KawaPaint.Engine;

namespace KawaPaint.App;

public partial class MainView
{
    /// <summary>Width, position, fill and opacity of the last stroke, offered again next time.
    /// The colours always start from the current primary and secondary colours.</summary>
    private StrokeOptions _strokeOptions = new();
    private bool _strokeOnNewLayer;

    private void OnStroke(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = StrokeAsync();

    private async Task StrokeAsync()
    {
        var doc = Canvas.Document;
        var layer = Canvas.ActiveLayer;
        if (doc is null || layer is null) return;
        RecordSkipped("Stroke");

        bool fromSelection = Canvas.Selection is { IsActive: true };
        byte[] shape = fromSelection ? Canvas.Selection!.Mask.ToArray() : StrokeOps.AlphaCoverage(layer.Surface);
        if (Array.FindIndex(shape, b => b >= 128) < 0)
        {
            StatusText.Text = fromSelection
                ? "Stroke: the selection is too faint to outline"
                : $"Stroke: {layer.Name} has no opaque pixels to outline - select an area first";
            return;
        }

        var snapshot = layer.Surface.Clone();
        StrokeField? field = null;
        Layer? strokeLayer = null;
        EffectBounds onLayer = default, onStrokeLayer = default;

        void Draw(StrokeOptions options, bool onNewLayer)
        {
            // The field depends only on the shape; it is rebuilt only when a wider stroke needs
            // more room around it, so the other settings preview instantly.
            if (field is null || field.Reach < options.Reach)
                field = StrokeField.Build(shape, doc.Width, doc.Height,
                    Math.Min(StrokeOps.MaxWidth, Math.Max(options.Reach * 1.5, 24)));

            var changed = onLayer.Union(onStrokeLayer);
            if (!onLayer.IsEmpty) layer.Surface.CopyRectFrom(snapshot, onLayer.X, onLayer.Y, onLayer.Width, onLayer.Height);
            if (strokeLayer is not null && !onStrokeLayer.IsEmpty)
                strokeLayer.Surface.ClearRect(onStrokeLayer.X, onStrokeLayer.Y, onStrokeLayer.Width, onStrokeLayer.Height, ColorBgra.Transparent);
            onLayer = onStrokeLayer = default;

            bool layersChanged = false;
            if (onNewLayer)
            {
                strokeLayer ??= new Layer(doc.Width, doc.Height, "Stroke");
                if (doc.IndexOf(strokeLayer) < 0)
                {
                    doc.InsertLayer(doc.IndexOf(layer) + 1, strokeLayer);
                    layersChanged = true;
                }
                onStrokeLayer = field.Render(strokeLayer.Surface, options);
            }
            else
            {
                if (strokeLayer is not null && doc.IndexOf(strokeLayer) >= 0)
                {
                    doc.RemoveLayer(strokeLayer);
                    layersChanged = true;
                }
                onLayer = field.Render(layer.Surface, options);
            }

            changed = changed.Union(onLayer).Union(onStrokeLayer);
            if (layersChanged) Canvas.RenderComposite();
            else if (!changed.IsEmpty) Canvas.RenderComposite(changed.X, changed.Y, changed.Width, changed.Height);
            Canvas.InvalidateVisual();
        }

        string source = fromSelection
            ? "Draws a contour around the selection."
            : $"Nothing is selected, so the contour follows the opaque pixels of {layer.Name}.";
        var initial = _strokeOptions with { Color1 = Canvas.BrushColor, Color2 = Canvas.SecondaryColor };
        var dialog = new StrokeDialog(source, initial, _strokeOnNewLayer, Draw);

        bool ok;
        try
        {
            ok = await ShowDialogAsync<bool>(dialog);
            if (ok)
            {
                // The preview is debounced, so draw the exact final settings before committing.
                Draw(dialog.Result, dialog.ResultOnNewLayer);
            }
        }
        catch
        {
            ok = false;
        }

        if (!ok)
        {
            layer.Surface.CopyFrom(snapshot);
            snapshot.Dispose();
            if (strokeLayer is not null)
            {
                doc.RemoveLayer(strokeLayer);
                strokeLayer.Dispose();
            }
            RefreshDocument();
            return;
        }

        _strokeOptions = dialog.Result;
        _strokeOnNewLayer = dialog.ResultOnNewLayer;

        if (_strokeOnNewLayer && strokeLayer is not null)
        {
            snapshot.Dispose();   // the active layer was never touched
            var added = strokeLayer;
            int index = doc.IndexOf(added);
            // The new layer is this step's own while it is undone - see OnAddLayer.
            Canvas.History.Push(new DelegateMemento("Stroke",
                undo: () =>
                {
                    doc.RemoveLayer(added);
                    if (ReferenceEquals(Canvas.ActiveLayer, added) && doc.LayerCount > 0)
                        Canvas.SetActiveLayer(doc.Layers[Math.Clamp(index - 1, 0, doc.LayerCount - 1)]);
                },
                redo: () => doc.InsertLayer(Math.Min(index, doc.LayerCount), added),
                approximateBytes: applied => applied ? 0 : SurfaceBytes(added.Surface),
                dispose: applied => { if (!applied) added.Dispose(); }));
            StatusText.Text = $"Stroke drawn on a new layer above {layer.Name}";
        }
        else
        {
            strokeLayer?.Dispose();   // created by a preview, then switched back off
            Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, "Stroke"));
            StatusText.Text = $"Stroke drawn on {layer.Name}";
        }
        RefreshDocument();
    }
}
