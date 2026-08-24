using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using KawaPaint.Engine;
using KawaPaint.Engine.MailMerge;

namespace KawaPaint.App;

public partial class MainView
{
    private async void OnDynamicTextRequested(int x, int y)
    {
        if (Canvas.Document is not { } doc || OwnerWindow is not { } owner) return;
        DynamicTextZone? existing = doc.DynamicTextZones.LastOrDefault(z =>
            x >= z.X && y >= z.Y && x < z.X + z.Width && y < z.Y + z.Height);
        var initial = existing?.Clone() ?? new DynamicTextZone
        {
            X = Math.Clamp(x, 0, Math.Max(0, doc.Width - 1)),
            Y = Math.Clamp(y, 0, Math.Max(0, doc.Height - 1)),
            Width = Math.Min(300, Math.Max(1, doc.Width - x)),
            Height = Math.Min(80, Math.Max(1, doc.Height - y)),
            Color = Canvas.BrushColor.ToHexString()
        };
        var editor = new DynamicTextZoneDialog(initial, existing is not null);
        int choice = await editor.ShowDialog<int>(owner);
        if (choice == 0) return;
        DynamicTextZone before = initial.Clone();
        if (choice == 2 && existing is not null)
        {
            int index = doc.DynamicTextZones.IndexOf(existing);
            doc.DynamicTextZones.RemoveAt(index);
            Canvas.History.Push(new DelegateMemento("Delete Dynamic Text Zone",
                () => { doc.DynamicTextZones.Insert(index, before.Clone()); Canvas.NotifyDynamicZonesChanged(); },
                () => { RemoveZone(doc, before.Id); Canvas.NotifyDynamicZonesChanged(); }));
        }
        else if (choice == 1)
        {
            DynamicTextZone after = editor.Result;
            if (existing is null)
            {
                int index = doc.DynamicTextZones.Count;
                doc.DynamicTextZones.Add(after);
                Canvas.History.Push(new DelegateMemento("Add Dynamic Text Zone",
                    () => { RemoveZone(doc, after.Id); Canvas.NotifyDynamicZonesChanged(); },
                    () => { doc.DynamicTextZones.Insert(Math.Min(index, doc.DynamicTextZones.Count), after.Clone()); Canvas.NotifyDynamicZonesChanged(); }));
            }
            else
            {
                ReplaceZone(doc, existing.Id, after);
                Canvas.History.Push(new DelegateMemento("Edit Dynamic Text Zone",
                    () => { ReplaceZone(doc, after.Id, before); Canvas.NotifyDynamicZonesChanged(); },
                    () => { ReplaceZone(doc, before.Id, after); Canvas.NotifyDynamicZonesChanged(); }));
            }
        }
        Canvas.NotifyDynamicZonesChanged();
    }

    private async void OnMailMerge(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas.Document is not { } doc) return;
        RecordSkipped("Mail Merge");
        if (OwnerWindow is not { } owner) { StatusText.Text = "Mail merge requires the desktop build"; return; }
        if (doc.DynamicTextZones.Count == 0)
        {
            StatusText.Text = "Add a Dynamic Text / CSV Zone to the canvas first";
            SelectTool("DynamicText");
            return;
        }
        var dialog = new MailMergeDialog(StorageProvider, _settings.Settings.ExportPresets, doc.DynamicTextZones.Count);
        if (await dialog.ShowDialog<bool>(owner) != true) return;
        if (!_settings.Settings.ExportPresets.TryGetValue(dialog.PresetName, out var preset)) return;

        try
        {
            StatusText.Text = "Creating mail-merge images…";
            CsvData data = CsvData.Load(dialog.CsvPath!);
            using var snapshot = doc.Clone();
            string sourceName = _currentFile?.Name ?? _session?.DisplayName ?? "template.kwp";
            var results = await Task.Run(() => MailMergeRunner.Run(snapshot, data, sourceName,
                dialog.PresetName, preset, dialog.OutputFolder!, dialog.FilenamePattern, KawaPaint.App.Core.AppPaths.Root));
            int failed = results.Count(r => r.Error is not null);
            string summary = string.Join(Environment.NewLine, results.Select(r => r.Error is null
                ? $"OK row {r.RowNumber}: {Path.GetFileName(r.OutputPath)}"
                : $"FAIL row {r.RowNumber}: {r.Error}"));
            summary += $"{Environment.NewLine}{Environment.NewLine}{results.Count - failed}/{results.Count} image(s) created.";
            StatusText.Text = $"Mail merge: {results.Count - failed}/{results.Count} created";
            await new BatchResultsDialog(summary).ShowDialog(owner);
        }
        catch (Exception ex) { StatusText.Text = "Mail merge failed: " + ex.Message; }
    }

    private static void RemoveZone(Document doc, Guid id)
    {
        var zone = doc.DynamicTextZones.FirstOrDefault(z => z.Id == id);
        if (zone is not null) doc.DynamicTextZones.Remove(zone);
    }

    private static void ReplaceZone(Document doc, Guid id, DynamicTextZone replacement)
    {
        for (int i = 0; i < doc.DynamicTextZones.Count; i++)
            if (doc.DynamicTextZones[i].Id == id) { doc.DynamicTextZones[i] = replacement.Clone(); return; }
    }
}
