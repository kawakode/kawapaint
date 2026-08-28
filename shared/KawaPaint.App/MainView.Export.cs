// KawaPaint - File > Export presets and local art-platform packages.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using KawaPaint.App.Core;
using KawaPaint.Engine.Publishing;
using KawaPaint.Engine;
using KawaPaint.Engine.Codecs;
using KawaPaint.Engine.Exporting;

namespace KawaPaint.App;

public partial class MainView
{
    private async void OnPublishArtwork(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas.Document is not { } document) return;
        if (OwnerWindow is not { } owner)
        {
            StatusText.Text = "Direct publishing is currently available in desktop builds";
            return;
        }

        RecordSkipped("Publish artwork");
        string sourceName = _currentFile?.Name ?? _session?.DisplayName ?? "untitled";
        var dialog = new PublishArtworkDialog(_settings, Path.GetFileNameWithoutExtension(sourceName));
        if (await dialog.ShowDialog<bool>(owner) != true || dialog.Selection is not { } selection) return;
        if (!_settings.Settings.ExportPresets.TryGetValue(selection.PresetName, out var preset))
        {
            StatusText.Text = "The selected export preset no longer exists";
            return;
        }

        string state = selection.State == PublishState.Published ? "publish" : selection.State.ToString().ToLowerInvariant();
        if (!await ConfirmAsync("Publish artwork",
                $"{char.ToUpperInvariant(state[0]) + state[1..]} this artwork to {selection.Target.Name} on {ProviderName(selection.ProviderId)}?",
                "Continue")) return;

        try
        {
            StatusText.Text = $"Preparing {selection.PresetName}…";
            using var snapshot = document.Clone();
            PresetUploadResult upload = await Task.Run(() => PresetExporter.ExportForUpload(
                snapshot, sourceName, selection.PresetName, preset, AppPaths.Root));
            var request = new ArtPublishRequest(upload.Bytes, upload.FileName, upload.MimeType,
                upload.Width, upload.Height, selection.Title, selection.Caption, selection.AltText,
                selection.Tags, selection.State, selection.IsMature, selection.MatureLevel,
                selection.MatureClassifications, selection.GalleryId, selection.IsAiGenerated,
                selection.NoAi);
            IArtPublisher publisher = selection.ProviderId switch
            {
                "tumblr" => new TumblrPublisher(),
                "deviantart" => new DeviantArtPublisher(),
                "facebook" => new FacebookPublisher(),
                _ => throw new InvalidOperationException("Unknown publishing platform.")
            };
            string accessToken = selection.Target.AccessToken ?? selection.Account.Token.AccessToken;
            StatusText.Text = $"Publishing to {selection.Target.Name}…";
            ArtPublishResult result = await publisher.PublishAsync(
                new PublishDestination(accessToken, selection.Target.Id, selection.Target.Name), request);
            StatusText.Text = result.Message + $" (ID {result.RemoteId})";
        }
        catch (ArtPublishException ex)
        {
            StatusText.Text = ex.Message + (ex.OutcomeMayBeAmbiguous ? " Do not retry until you check the platform." : "");
        }
        catch (Exception ex) { StatusText.Text = "Publishing failed: " + ex.Message; }
    }

    private static string ProviderName(string id) => id switch
    {
        "tumblr" => "Tumblr", "deviantart" => "DeviantArt", "facebook" => "Facebook", _ => id
    };

    private static readonly FilePickerFileType AnimatedGifFileType = new("Animated GIF")
    {
        Patterns = new[] { "*.gif" }
    };
    private static readonly FilePickerFileType AnimatedPngFileType = new("Animated PNG (APNG)")
    {
        Patterns = new[] { "*.png" }
    };
    private static readonly FilePickerFileType AnimatedWebPFileType = new("Animated WebP")
    {
        Patterns = new[] { "*.webp" }
    };

    private async void OnExportAnimatedGif(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Canvas.Document is not { } document) return;
        int frameCount = document.FrameCount;
        RecordSkipped("Export Animation");

        bool loop;
        if (OwnerWindow is { } owner)
        {
            var dialog = new AnimationExportDialog(frameCount);
            if (!await dialog.ShowDialog<bool>(owner)) return;
            loop = dialog.Loop;
        }
        else
        {
            var values = await ShowCanvasAnimationSettingsAsync(frameCount);
            if (values is null) return;
            loop = values.Loop;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Animation",
            SuggestedFileName = "animation.gif",
            DefaultExtension = "gif",
            FileTypeChoices = new[] { AnimatedGifFileType, AnimatedPngFileType, AnimatedWebPFileType }
        });
        if (file is null) return;

        List<Surface> frames = AnimatedGifEncoder.RenderDocumentFrames(document);
        int[] durations = document.Frames.Select(frame => frame.DurationMs).ToArray();
        try
        {
            await using var stream = await file.OpenWriteAsync();
            switch (Path.GetExtension(file.Name).ToLowerInvariant())
            {
                case ".png":
                    AnimatedImageEncoder.EncodeApng(frames, durations, stream, loop);
                    break;
                case ".webp":
                    AnimatedImageEncoder.EncodeWebP(frames, durations, stream, loop);
                    break;
                default:
                    AnimatedGifEncoder.Encode(frames, stream, durations, loop: loop, dither: true);
                    break;
            }
            StatusText.Text = $"Exported {frames.Count}-frame animation: {file.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Animation export failed: " + ex.Message;
        }
        finally
        {
            foreach (Surface frame in frames) frame.Dispose();
        }
    }

    private void RebuildExportPresetsMenu()
    {
        // The final five entries are the two fixed exports, separator, merge and manager items.
        while (ExportPresetsMenu.Items.Count > 5) ExportPresetsMenu.Items.RemoveAt(0);

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
