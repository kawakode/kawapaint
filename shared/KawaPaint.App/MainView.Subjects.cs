// KawaPaint - Select Subject, Remove Background and Isolate Subjects to Layers.
//
// All three run SubjectSegmentation (GrabCut, no trained model) on the active layer. An active
// selection is used as the hint: only what it covers can be a subject, so a rough lasso around
// the subject steers the result when the automatic guess picks the wrong thing.

using System;
using System.Linq;
using System.Threading.Tasks;
using KawaPaint.Engine;

namespace KawaPaint.App;

public partial class MainView
{
    /// <summary>More subjects than this go unlisted: past it they are noise, not subjects.</summary>
    private const int MaxIsolatedSubjects = 12;

    private bool _findingSubject;

    private void OnSelectSubject(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = SelectSubjectAsync();

    private void OnRemoveBackground(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = RemoveBackgroundAsync();

    private void OnIsolateSubjects(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = IsolateSubjectsAsync();

    /// <summary>Runs the segmentation off the UI thread on a copy of the active layer. Null when
    /// there is nothing to work on, another run is going, or the document changed meanwhile.</summary>
    private async Task<(Document Doc, Layer Layer, SubjectMask Mask)?> FindSubjectAsync(string name)
    {
        var doc = Canvas.Document;
        var layer = Canvas.ActiveLayer;
        if (doc is null || layer is null || _findingSubject) return null;

        bool hinted = Canvas.Selection is { IsActive: true };
        byte[]? hint = hinted ? Canvas.Selection!.Mask.ToArray() : null;
        var pixels = layer.Surface.Clone();
        var options = new SubjectOptions { WorkingSize = OperatingSystem.IsBrowser() ? 384 : 512 };

        _findingSubject = true;
        StatusText.Text = name + ": looking for the subject…";
        SubjectMask mask;
        try
        {
            mask = await Task.Run(() => SubjectSegmentation.Find(pixels, hint, options));
        }
        catch (Exception ex)
        {
            StatusText.Text = name + " failed: " + ex.Message;
            return null;
        }
        finally
        {
            pixels.Dispose();
            _findingSubject = false;
        }

        if (!ReferenceEquals(Canvas.Document, doc) || doc.IndexOf(layer) < 0
            || mask.Width != doc.Width || mask.Height != doc.Height)
        {
            StatusText.Text = name + ": the image changed while looking, so nothing was applied";
            return null;
        }
        if (mask.IsEmpty)
        {
            StatusText.Text = hinted
                ? name + ": no subject stands out inside the selection"
                : name + ": no subject stands out - select roughly around it and try again";
            return null;
        }
        return (doc, layer, mask);
    }

    private async Task SelectSubjectAsync()
    {
        RecordSkipped("Select Subject");
        if (await FindSubjectAsync("Select Subject") is not { } found) return;
        if (Canvas.Selection is not { } selection) return;
        Canvas.ChangeSelection("Select Subject", () => SubjectSegmentation.ApplyTo(selection, found.Mask.Coverage));
        StatusText.Text = found.Mask.SubjectCount == 1
            ? "Subject selected"
            : $"{found.Mask.SubjectCount} subjects selected";
    }

    private async Task RemoveBackgroundAsync()
    {
        // Scripts replay it on whole images (batch background removal); demos skip it, since the
        // search runs asynchronously and a replay would race past it.
        _scriptRecorder.NoteAction("image.removeBackground");
        RecordSkipped("Remove Background");
        if (await FindSubjectAsync("Remove Background") is not { } found) return;
        var (_, layer, mask) = found;

        var snapshot = layer.Surface.Clone();
        SubjectSegmentation.ApplyAsAlpha(layer.Surface, mask.Coverage);
        Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, "Remove Background"));
        RefreshDocument();
        StatusText.Text = $"Background removed from {layer.Name}";
    }

    private async Task IsolateSubjectsAsync()
    {
        RecordSkipped("Isolate Subjects to Layers");
        if (await FindSubjectAsync("Isolate Subjects") is not { } found) return;
        var (doc, source, mask) = found;

        int count = Math.Min(mask.SubjectCount, MaxIsolatedSubjects);
        var layers = new Layer[count];
        for (int k = 0; k < count; k++)
        {
            var (coverage, _, _, _, _) = mask.SubjectCoverage(k);
            var surface = source.Surface.Clone();
            SubjectSegmentation.ApplyAsAlpha(surface, coverage);
            layers[k] = new Layer(surface, count == 1 ? "Subject" : $"Subject {k + 1}")
            {
                BlendMode = source.BlendMode,
                Opacity = source.Opacity
            };
        }

        int index = doc.IndexOf(source);
        bool wasVisible = source.Visible;
        void Apply()
        {
            // Largest subject on top, right above the layer it came from.
            for (int k = count - 1; k >= 0; k--) doc.InsertLayer(index + 1, layers[k]);
            source.Visible = false;
            Canvas.SetActiveLayer(layers[0]);
        }
        void Revert()
        {
            foreach (var layer in layers) doc.RemoveLayer(layer);
            source.Visible = wasVisible;
            Canvas.SetActiveLayer(source);
        }
        Apply();

        // The new layers are this step's own while it is undone - see OnAddLayer.
        Canvas.History.Push(new DelegateMemento("Isolate Subjects",
            undo: Revert,
            redo: Apply,
            approximateBytes: applied => applied ? 0 : layers.Sum(l => SurfaceBytes(l.Surface)),
            dispose: applied => { if (!applied) foreach (var layer in layers) layer.Dispose(); }));

        RefreshDocument();
        StatusText.Text = count == 1
            ? $"Subject copied to its own layer; {source.Name} is hidden"
            : $"{count} subjects copied to their own layers; {source.Name} is hidden";
    }
}
