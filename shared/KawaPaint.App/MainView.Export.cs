// KawaPaint - File > Export presets and local art-platform packages.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using KawaPaint.App.Core;
using KawaPaint.Engine.Exporting;

namespace KawaPaint.App;

public partial class MainView
{
    private void RebuildExportPresetsMenu()
    {
        // The final four entries are the fixed flattened export, separator, merge and manager items.
        while (ExportPresetsMenu.Items.Count > 4) ExportPresetsMenu.Items.RemoveAt(0);

        foreach (var (name, preset) in _settings.Settings.ExportPresets
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Reverse())
        {
            var codec = KawaPaint.Engine.Codecs.CodecRegistry.FindById(preset.CodecId);
            bool available = codec is { CanEncode: true, IsAvailable: true };
            var item = new MenuItem
            {
                Header = name + (available ? "" : " (unavailable)"),
                Tag = name,
                IsEnabled = available
            };
            ToolTip.SetTip(item, available ? $"Export using the {codec!.DisplayName} preset" :
                $"The {preset.CodecId} encoder is unavailable on this platform");
            item.Click += OnRunExportPreset;
            ExportPresetsMenu.Items.Insert(0, item);
        }
    }

    private async void OnManageExportPresets(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (OwnerWindow is not { } owner)
        {
            StatusText.Text = "Export preset management isn't available in the browser build yet";
            return;
        }

        var dialog = new ExportPresetManagerDialog(_settings.Settings.ExportPresets, StorageProvider);
        if (await dialog.ShowDialog<bool>(owner) != true) return;

        _settings.Update(s => s.ExportPresets = dialog.ResultPresets
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
        RebuildExportPresetsMenu();
        StatusText.Text = $"Saved {dialog.ResultPresets.Count} export preset(s)";
    }

    private async void OnRunExportPreset(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string name } || Canvas.Document is null) return;
        if (!_settings.Settings.ExportPresets.TryGetValue(name, out var preset)) return;
        RecordSkipped("Export preset " + name);

        string? outputFolder = preset.OutputFolder;
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            if (OwnerWindow is null)
            {
                StatusText.Text = "This preset needs an output folder";
                return;
            }
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = $"Export with {name}", AllowMultiple = false });
            var folder = folders.FirstOrDefault();
            outputFolder = folder is null ? null : LocalPathOf(folder);
            if (outputFolder is null)
            {
                if (folder is not null) StatusText.Text = "This export needs a local filesystem folder";
                return;
            }
        }

        string sourceName = _currentFile?.Name ?? _session?.DisplayName ?? "untitled";
        try
        {
            StatusText.Text = $"Exporting {name}…";
            using var snapshot = Canvas.Document.Clone();
            PresetExportResult result = await Task.Run(() => PresetExporter.ExportFile(
                snapshot, sourceName, name, preset, outputFolder, AppPaths.Root));

            if (preset.CopyPackageTextToClipboard && result.SidecarPath is not null &&
                TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(await File.ReadAllTextAsync(result.SidecarPath));

            int issues = result.ScriptSteps.Count(s => s.Outcome != KawaPaint.Engine.Scripting.ScriptStepOutcome.Applied);
            StatusText.Text = $"Exported {Path.GetFileName(result.OutputPath)} ({result.Width}×{result.Height})" +
                (result.SidecarPath is null ? "" : " + caption") +
                (issues == 0 ? "" : $"; {issues} script issue(s)");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Preset export failed: " + ex.Message;
        }
    }
}
