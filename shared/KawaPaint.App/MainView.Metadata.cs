// KawaPaint - File ▸ Metadata…: inspect what EXIF/IPTC/XMP the chosen files carry, and remove it
// without re-encoding them.
//
// Note this operates on *files*, not on the open document, and that is deliberate. KawaPaint has
// never carried metadata through a decode - the codecs hand back a Surface and nothing else - so
// the open document has no metadata to show or to strip, and anything exported from it is already
// clean. The gap this fills is the other case: a photo on disk you want cleaned without paying a
// generation of JPEG quality to do it. Engine/Metadata/ does the container surgery; this file is
// only the file-picker + progress + results wiring, matching MainView.Script.cs's batch path.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using KawaPaint.Engine.Metadata;

namespace KawaPaint.App;

public partial class MainView
{
    private static readonly FilePickerFileType MetadataFileType = new("Images with metadata")
    {
        Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" }
    };

    private async void OnMetadata(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (OwnerWindow is not { } owner) { StatusText.Text = "Metadata tools aren't available in the browser build yet"; return; }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose image file(s) to inspect",
            AllowMultiple = true,
            FileTypeFilter = new[] { MetadataFileType }
        });
        if (files.Count == 0) return;

        StatusText.Text = $"Inspecting {files.Count} file(s)…";

        // Scan pass keeps only the reports, not the file bytes: the strip pass re-reads. A re-read
        // costs nothing next to holding every selected photo in memory at once, and the picker
        // happily allows selecting a whole folder of them.
        var scans = new List<(string Name, MetadataReport Report)>();
        foreach (var file in files)
        {
            try
            {
                scans.Add((file.Name, MetadataScanner.Scan(await ReadAllBytesAsync(file))));
            }
            catch (Exception ex)
            {
                scans.Add((file.Name + $"  [unreadable: {ex.Message}]", new MetadataReport()));
            }
        }

        var dialog = new MetadataDialog(StorageProvider, scans);
        if (await dialog.ShowDialog<bool>(owner) != true)
        {
            StatusText.Text = "Ready";
            return;
        }

        var options = dialog.Options;
        var log = new StringBuilder();
        int cleaned = 0, skipped = 0, failed = 0, bytesRemoved = 0;

        foreach (var file in files)
        {
            try
            {
                byte[] bytes = await ReadAllBytesAsync(file);
                var result = MetadataStripper.Strip(bytes, options);

                if (!result.Changed)
                {
                    // Distinguish "clean already" from "could not be read", since only the second
                    // is a reason for the user to go and do something about the file.
                    var report = MetadataScanner.Scan(bytes);
                    log.AppendLine(report.CanStrip
                        ? $"--  {file.Name}: nothing to remove"
                        : $"!!  {file.Name}: not a container this can rewrite safely - left alone");
                    skipped++;
                    continue;
                }

                if (dialog.InPlace) await WriteInPlaceAsync(file, result.Bytes);
                else await WriteCopyAsync(dialog.OutputFolder!, file.Name, result.Bytes);

                cleaned++;
                bytesRemoved += result.BytesRemoved;
                log.AppendLine($"OK  {file.Name}: removed {result.Removed.Count} block(s), " +
                               $"{MetadataReport.FormatSize(result.BytesRemoved)} " +
                               $"({string.Join(", ", result.Removed.Select(b => b.Label))})");
            }
            catch (Exception ex)
            {
                failed++;
                log.AppendLine($"!!  {file.Name}: {ex.Message}");
            }
        }

        log.AppendLine();
        log.AppendLine($"{files.Count} file(s): {cleaned} cleaned, {skipped} skipped, {failed} failed" +
                       (bytesRemoved > 0 ? $", {MetadataReport.FormatSize(bytesRemoved)} removed" : ""));

        StatusText.Text = $"Metadata: {cleaned}/{files.Count} cleaned";
        await new BatchResultsDialog(log.ToString()) { Title = "Metadata Results" }.ShowDialog(owner);
    }

    private static async Task<byte[]> ReadAllBytesAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Overwrites the picked file. Where the picker resolved to a real local path this goes through
    /// <see cref="MetadataStripper.StripFile"/>'s temp-file-then-move, so an interrupted write can't
    /// leave a truncated photo behind. The stream fallback exists for pickers that hand back no
    /// path at all, and truncates explicitly - the cleaned bytes are always shorter than the
    /// original, so a plain write would otherwise leave the tail of the old file in place.
    /// </summary>
    private static async Task WriteInPlaceAsync(IStorageFile file, byte[] bytes)
    {
        if (LocalPathOf(file) is { } path)
        {
            string dir = Path.GetDirectoryName(path) is { Length: > 0 } d ? d : ".";
            string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
                throw;
            }
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        await stream.WriteAsync(bytes);
    }

    private static async Task WriteCopyAsync(IStorageFolder folder, string name, byte[] bytes)
    {
        var outFile = await folder.CreateFileAsync(name)
            ?? throw new IOException("Could not create the output file.");
        await using var stream = await outFile.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        await stream.WriteAsync(bytes);
    }
}
