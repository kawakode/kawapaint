// KawaPaint - script recording and batch-apply, wired into the editor. Structural twin of
// MainView.Demo.cs, but far smaller: a script has no starting document to embed and no playback
// timeline (batch runs synchronously to completion, not through a DispatcherTimer), and unlike
// Demo Play - which replaces whatever is open on the canvas - Batch Apply Script never touches
// Canvas.Document at all: every target file is decoded, run, and saved as its own throwaway
// Document, entirely independent of the live editing session.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using KawaPaint.Engine;
using KawaPaint.Engine.Codecs;
using KawaPaint.Engine.Scripting;

namespace KawaPaint.App;

public partial class MainView
{
    private static readonly FilePickerFileType ScriptFileType = new("KawaPaint script")
    {
        Patterns = new[] { "*" + ScriptFile.Extension }
    };

    public bool IsRecordingScript => _scriptRecorder.IsRecording;

    private void InitializeScript()
    {
        _scriptRecorder.Progress += UpdateScriptStatus;
    }

    // ---- start / stop recording --------------------------------------------

    private void OnScriptRecord(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_scriptRecorder.IsRecording) { _ = StopAndSaveScriptAsync(); return; }

        if (_demoRecorder.IsRecording) { StatusText.Text = "Stop recording the demo before recording a script"; return; }
        if (_demoPlayer.IsActive) { StatusText.Text = "Stop demo playback before recording a script"; return; }

        _scriptRecorder.Start(typeof(MainView).Assembly.GetName().Version?.ToString() ?? "");
        UpdateScriptMenuState();
        StatusText.Text = "Recording script - File ▸ Record ▸ Stop & Save Script to finish";
    }

    private async Task StopAndSaveScriptAsync()
    {
        var script = _scriptRecorder.Stop();
        UpdateScriptMenuState();
        if (script is null) return;

        if (script.Steps.Count == 0) { StatusText.Text = "Nothing scriptable was recorded"; return; }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Script",
            SuggestedFileName = "script" + ScriptFile.Extension,
            DefaultExtension = ScriptFile.Extension.TrimStart('.'),
            FileTypeChoices = new[] { ScriptFileType }
        });
        if (file is null) { StatusText.Text = "Script discarded"; return; }

        script.Title = file.Name;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            script.Save(stream);
            StatusText.Text = $"Script saved: {script.Steps.Count} step(s)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Script save failed: " + ex.Message;
        }
    }

    // ---- batch apply ----------------------------------------------------------

    private async void OnBatchApplyScript(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_scriptRecorder.IsRecording) { StatusText.Text = "Stop recording the script first"; return; }
        if (_demoRecorder.IsRecording) { StatusText.Text = "Stop recording the demo first"; return; }
        if (OwnerWindow is not { } owner) { StatusText.Text = "Batch apply isn't available in the browser build yet"; return; }

        var scriptFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a script to apply",
            AllowMultiple = false,
            FileTypeFilter = new[] { ScriptFileType }
        });
        if (scriptFiles.Count == 0) return;

        ScriptFile script;
        try
        {
            await using var stream = await scriptFiles[0].OpenReadAsync();
            script = ScriptFile.Load(stream);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not read script: " + ex.Message;
            return;
        }

        var targetFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose target file(s)",
            AllowMultiple = true,
            FileTypeFilter = BuildOpenFilters()
                .Append(new FilePickerFileType("KawaPaint project") { Patterns = new[] { "*" + DocumentFile.Extension } })
                .ToArray()
        });
        if (targetFiles.Count == 0) return;

        var options = new BatchApplyDialog(StorageProvider);
        if (await options.ShowDialog<bool>(owner) != true) return;

        var policy = options.StopOnError ? ScriptFailurePolicy.StopOnError : ScriptFailurePolicy.ContinueOnError;

        StatusText.Text = $"Applying script to {targetFiles.Count} file(s)…";
        var results = new List<(string Name, string? Error, IReadOnlyList<ScriptStepResult> Steps)>();

        foreach (var target in targetFiles)
        {
            Document? workingDoc = null;
            try
            {
                workingDoc = await DecodeAsync(target);
                var steps = ScriptExecutor.Run(ref workingDoc, script, policy);

                if (options.InPlace)
                {
                    await using var outStream = await target.OpenWriteAsync();
                    Encode(workingDoc, outStream, target.Name);
                }
                else
                {
                    var outFile = await options.OutputFolder!.CreateFileAsync(target.Name)
                        ?? throw new IOException("Could not create the output file.");
                    await using var outStream = await outFile.OpenWriteAsync();
                    Encode(workingDoc, outStream, target.Name);
                }

                results.Add((target.Name, null, steps));
            }
            catch (Exception ex)
            {
                results.Add((target.Name, ex.Message, Array.Empty<ScriptStepResult>()));
            }
            finally
            {
                workingDoc?.Dispose();
            }
        }

        string summary = BuildResultsSummary(results);
        int failed = results.Count(r => r.Error is not null);
        StatusText.Text = $"Batch apply: {results.Count - failed}/{results.Count} saved";
        await new BatchResultsDialog(summary).ShowDialog(owner);
    }

    private async Task<Document> DecodeAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        if (file.Name.EndsWith(DocumentFile.Extension, StringComparison.OrdinalIgnoreCase))
            return DocumentFile.Load(stream);

        using var surface = CodecRegistry.Decode(stream, file.Name);
        var doc = new Document(surface.Width, surface.Height);
        var layer = doc.AddLayer();
        layer.Surface.CopyFrom(surface);
        return doc;
    }

    private static void Encode(Document doc, Stream outStream, string name)
    {
        if (name.EndsWith(DocumentFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            DocumentFile.Save(doc, outStream);
            return;
        }
        using var flat = doc.Flatten();
        CodecRegistry.Encode(flat, outStream, name);
    }

    private static string BuildResultsSummary(List<(string Name, string? Error, IReadOnlyList<ScriptStepResult> Steps)> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            if (r.Error is not null) { sb.AppendLine($"FAIL  {r.Name}: {r.Error}"); continue; }

            var issues = r.Steps.Where(s => s.Outcome is ScriptStepOutcome.SkippedNotApplicable
                or ScriptStepOutcome.SkippedUnknownId or ScriptStepOutcome.Failed).ToList();
            if (issues.Count == 0)
            {
                sb.AppendLine($"OK    {r.Name}  ({r.Steps.Count} step(s))");
            }
            else
            {
                sb.AppendLine($"WARN  {r.Name}  ({issues.Count} step(s) skipped/failed)");
                foreach (var s in issues)
                    sb.AppendLine($"        step {s.StepIndex} '{s.Id}': {s.Outcome}" + (s.Message is null ? "" : " - " + s.Message));
            }
        }

        int ok = results.Count(r => r.Error is null);
        sb.AppendLine();
        sb.AppendLine($"{results.Count} file(s): {ok} saved, {results.Count - ok} failed.");
        return sb.ToString();
    }

    // ---- status / menu state ---------------------------------------------

    private void UpdateScriptStatus()
    {
        if (_scriptRecorder.IsRecording)
            DemoStatusText.Text = $"● REC SCRIPT  ({_scriptRecorder.StepCount} step(s))";
    }

    private void UpdateScriptMenuState()
    {
        bool recording = _scriptRecorder.IsRecording;
        ScriptRecordItem.Header = recording ? "Stop & _Save Script…" : "Record _Script";
        BatchApplyScriptItem.IsEnabled = !recording;
        UpdateScriptStatus();
        if (!recording) UpdateDemoStatus();
    }
}
