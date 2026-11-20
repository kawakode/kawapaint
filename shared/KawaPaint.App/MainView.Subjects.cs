// KawaPaint - Select Subject, Remove Background and Isolate Subjects to Layers.
//
// All three run BackgroundRemoval (GrabCut with no trained model, then a full-size clean-up of
// the background colours left inside the subject and along its edges) on the active layer. An
// active selection is used as the hint: only what it covers can be a subject, so a rough lasso
// around the subject steers the result when the automatic guess picks the wrong thing.
//
// Remove Background opens a dialog first (eyedropper for the background colours, tolerance,
// enclosed areas, edges), previewed live on the canvas; RemoveBackgroundAsync applies the
// defaults straight away, as the script step does.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KawaPaint.Engine;

namespace KawaPaint.App;

public partial class MainView
{
    /// <summary>More subjects than this go unlisted: past it they are noise, not subjects.</summary>
    private const int MaxIsolatedSubjects = 12;

    private bool _findingSubject;

    private void OnSelectSubject(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = SelectSubjectAsync();

    private void OnRemoveBackground(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = RemoveBackgroundWithDialogAsync();

    private void OnIsolateSubjects(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = IsolateSubjectsAsync();

    private SubjectOptions SubjectSearchOptions() => new() { WorkingSize = OperatingSystem.IsBrowser() ? 384 : 512 };

    /// <summary>Runs the search off the UI thread on a copy of the active layer, with the default
    /// settings. Null when there is nothing to work on, another run is going, or the document
    /// changed meanwhile.</summary>
    private async Task<(Document Doc, Layer Layer, BackgroundCut Cut)?> FindSubjectAsync(string name)
    {
        var doc = Canvas.Document;
        var layer = Canvas.ActiveLayer;
        if (doc is null || layer is null || _findingSubject) return null;

        bool hinted = Canvas.Selection is { IsActive: true };
        byte[]? hint = hinted ? Canvas.Selection!.Mask.ToArray() : null;
        var pixels = layer.Surface.Clone();
        var options = SubjectSearchOptions();

        _findingSubject = true;
        StatusText.Text = name + ": looking for the subject…";
        BackgroundCut cut;
        try
        {
            cut = await Task.Run(() => BackgroundRemoval.Compute(pixels, BackgroundOptions.Default, hint, options));
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
            || cut.Width != doc.Width || cut.Height != doc.Height)
        {
            StatusText.Text = name + ": the image changed while looking, so nothing was applied";
            return null;
        }
        if (cut.Subject is { IsEmpty: true } || cut.IsEmpty)
        {
            StatusText.Text = NoSubjectMessage(name, hinted);
            return null;
        }
        return (doc, layer, cut);
    }

    private static string NoSubjectMessage(string name, bool hinted) => hinted
        ? name + ": no subject stands out inside the selection"
        : name + ": no subject stands out - select roughly around it and try again";

    private async Task SelectSubjectAsync()
    {
        RecordSkipped("Select Subject");
        if (await FindSubjectAsync("Select Subject") is not { } found) return;
        if (Canvas.Selection is not { } selection) return;
        Canvas.ChangeSelection("Select Subject", () => SubjectSegmentation.ApplyTo(selection, found.Cut.Coverage));
        int count = found.Cut.Subject?.SubjectCount ?? 1;
        StatusText.Text = count == 1 ? "Subject selected" : $"{count} subjects selected";
    }

    /// <summary>Remove Background with the default settings, no dialog.</summary>
    private async Task RemoveBackgroundAsync()
    {
        // Scripts replay it on whole images (batch background removal); demos skip it, since the
        // search runs asynchronously and a replay would race past it.
        _scriptRecorder.NoteAction("image.removeBackground");
        RecordSkipped("Remove Background");
        if (await FindSubjectAsync("Remove Background") is not { } found) return;
        var (_, layer, cut) = found;

        var snapshot = layer.Surface.Clone();
        cut.ApplyTo(layer.Surface);
        Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, "Remove Background"));
        RefreshDocument();
        StatusText.Text = $"Background removed from {layer.Name}";
    }

    private async Task RemoveBackgroundWithDialogAsync()
    {
        var doc = Canvas.Document;
        var layer = Canvas.ActiveLayer;
        if (doc is null || layer is null || _findingSubject) return;
        _scriptRecorder.NoteAction("image.removeBackground");
        RecordSkipped("Remove Background");

        bool hinted = Canvas.Selection is { IsActive: true };
        byte[]? hint = hinted ? Canvas.Selection!.Mask.ToArray() : null;
        var snapshot = layer.Surface.Clone();     // the source of every preview, read-only until the end
        var searchOptions = SubjectSearchOptions();

        // Previews run one at a time; a newer one makes an older result stale. The subject search
        // (the slow part) is redone only when the method or the background colours change.
        var gate = new SemaphoreSlim(1, 1);
        int version = 0;
        bool closed = false;
        SubjectMask? subject = null;
        string? subjectKey = null;
        BackgroundCut? shown = null;
        BackgroundOptions? shownOptions = null;
        RemoveBackgroundDialog? dialog = null;

        static string KeyOf(BackgroundOptions o)
            => o.Mode + ":" + string.Join(",", o.Colors.Select(c => c.Bgra.ToString("X8")));

        BackgroundCut Compute(BackgroundOptions o)
        {
            if (o.Mode == BackgroundMode.ColorsOnly) return BackgroundRemoval.Compute(snapshot, o, hint, searchOptions);
            string key = KeyOf(o);
            if (subject is null || subjectKey != key)
            {
                var seeded = new SubjectOptions { WorkingSize = searchOptions.WorkingSize, BackdropColors = o.Colors };
                subject = SubjectSegmentation.Find(snapshot, hint, seeded);
                subjectKey = key;
            }
            return BackgroundRemoval.Refine(snapshot, subject, o, hint);
        }

        void Show(BackgroundCut cut)
        {
            layer.Surface.CopyFrom(snapshot);
            cut.ApplyTo(layer.Surface);
            Canvas.RenderComposite();
            Canvas.InvalidateVisual();
        }

        string Describe(BackgroundCut cut, BackgroundOptions o)
        {
            if (cut.Subject is { IsEmpty: true } || cut.IsEmpty)
                return o.Mode == BackgroundMode.ColorsOnly
                    ? "Nothing would be left - lower the tolerance or pick other colors"
                    : hinted
                        ? "No subject stands out inside the selection - pick the background color, or try Background colors only"
                        : "No subject stands out - pick the background color, or select roughly around the subject first";
            if (!cut.Backdrop.Usable)
                return "The background is not a plain color, so only the subject search applies. "
                    + "Pick its color to also clear what is left of it.";
            string colors = cut.Backdrop.UserChosen ? "picked" : "found on the borders";
            return $"Background colors {colors}: {cut.Backdrop.Colors.Length}.";
        }

        async void Preview(BackgroundOptions o)
        {
            int mine = ++version;
            dialog?.SetStatus(o.Mode == BackgroundMode.Subject && (subject is null || subjectKey != KeyOf(o))
                ? "Looking for the subject…" : "Updating…");
            await gate.WaitAsync();
            try
            {
                if (mine != version || closed) return;
                var cut = await Task.Run(() => Compute(o));
                if (mine != version || closed) return;
                shown = cut; shownOptions = o;
                Show(cut);
                dialog?.SetAutoTolerance(BackgroundRemoval.AutoTolerance(cut.Backdrop));
                dialog?.SetStatus(Describe(cut, o));
            }
            catch (Exception ex)
            {
                if (!closed) dialog?.SetStatus("Preview failed: " + ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }

        _findingSubject = true;
        bool ok;
        BackgroundOptions final;
        try
        {
            var backdrop = await Task.Run(() => SubjectSegmentation.FindBackdrop(snapshot, searchOptions));
            string description = hinted
                ? "Keeps the subject inside the selection and makes the rest transparent."
                : $"Keeps the subject of {layer.Name} and makes the background transparent.";
            dialog = new RemoveBackgroundDialog(description, snapshot, BackgroundRemoval.AutoTolerance(backdrop), Preview);
            ok = await ShowDialogAsync<bool>(dialog);
            final = dialog.Result;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Remove Background failed: " + ex.Message;
            ok = false;
            final = BackgroundOptions.Default;
        }

        // Let a preview that is running finish, then take over.
        version++;
        await gate.WaitAsync();
        closed = true;
        try
        {
            BackgroundCut? cut = null;
            if (ok)
            {
                try
                {
                    cut = shown is not null && shownOptions is not null && SameSettings(shownOptions, final)
                        ? shown
                        : await Task.Run(() => Compute(final));
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Remove Background failed: " + ex.Message;
                }
            }

            if (cut is null || cut.Subject is { IsEmpty: true } || cut.IsEmpty)
            {
                layer.Surface.CopyFrom(snapshot);
                snapshot.Dispose();
                RefreshDocument();
                if (ok && cut is not null) StatusText.Text = NoSubjectMessage("Remove Background", hinted);
                return;
            }

            Show(cut);
            Canvas.History.Push(TileDeltaMemento.Consume(layer, snapshot, "Remove Background"));
            RefreshDocument();
            StatusText.Text = $"Background removed from {layer.Name}";
        }
        finally
        {
            _findingSubject = false;
            gate.Release();
        }
    }

    private static bool SameSettings(BackgroundOptions a, BackgroundOptions b)
        => a with { Colors = [] } == b with { Colors = [] } && a.Colors.SequenceEqual(b.Colors);

    private async Task IsolateSubjectsAsync()
    {
        RecordSkipped("Isolate Subjects to Layers");
        if (await FindSubjectAsync("Isolate Subjects") is not { } found) return;
        var (doc, source, cut) = found;
        if (cut.Subject is not { } mask) return;

        int count = Math.Min(mask.SubjectCount, MaxIsolatedSubjects);
        var layers = new Layer[count];
        for (int k = 0; k < count; k++)
        {
            var coverage = cut.SubjectCoverage(k);
            var surface = source.Surface.Clone();
            cut.ApplyTo(surface, coverage);
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
