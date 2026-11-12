// KawaPaint - where recovery snapshots are kept, and which of them are leftovers.
//
// Desktop and Android keep them as project files in a folder; the browser head has no filesystem
// and installs its own store. A snapshot is a "leftover" - work to offer back at startup - only
// when the copy of the app that wrote it is gone: snapshots of a copy that is still running are
// that copy's safety net and must be neither offered nor deleted by another one.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KawaPaint.Engine;

namespace KawaPaint.App.Core;

/// <summary>The newest snapshot of one document left behind by a previous run.</summary>
public sealed record RecoveryEntry(string SessionId, string Path, DateTime WrittenUtc);

public interface IRecoveryStore
{
    /// <summary>Writes a snapshot of the document identified by <paramref name="sessionId"/>,
    /// keeping at most <paramref name="keepVersions"/> of them. Encoding runs off the UI thread.</summary>
    Task SaveAsync(string sessionId, Document snapshot, int keepVersions, CancellationToken cancellation);

    /// <summary>One entry per document whose owner is gone, newest first.</summary>
    Task<IReadOnlyList<RecoveryEntry>> FindLeftoversAsync();

    /// <summary>Reads a leftover back. Throws when none of its versions can be read; the entry
    /// is left in place either way.</summary>
    Task<Document> LoadAsync(RecoveryEntry entry);

    /// <summary>Deletes every snapshot of one document - once it is safely saved, restored or
    /// explicitly discarded.</summary>
    Task DiscardAsync(string sessionId);
}

/// <summary>Process-wide store selection, like <see cref="SettingsStore"/>.</summary>
public static class RecoveryStore
{
    private static IRecoveryStore? _override;
    private static readonly FileRecoveryStore Files =
        new(() => SettingsService.Instance.Settings.Autosave.RecoveryDirectory);

    /// <summary>Null when this platform has nowhere to keep snapshots.</summary>
    public static IRecoveryStore? Current
    {
        get => _override ?? (AppPaths.Root is null ? null : Files);
        set => _override = value;
    }
}

/// <summary>Snapshots as project files: one folder per document, one file per version.</summary>
public sealed class FileRecoveryStore : IRecoveryStore
{
    private const string OwnerFile = "owner";

    private readonly Func<string?> _customDirectory;

    /// <param name="customDirectory">The user's own recovery folder, or null for the default one
    /// beside the settings.</param>
    public FileRecoveryStore(Func<string?>? customDirectory = null)
        => _customDirectory = customDirectory ?? (() => null);

    private string? WriteRoot
    {
        get
        {
            string? custom = _customDirectory();
            return string.IsNullOrWhiteSpace(custom) ? AppPaths.RecoveryDirectory : custom;
        }
    }

    // The default folder is searched even when a custom one is set, so snapshots written before
    // the setting changed are still found.
    private IEnumerable<string> SearchRoots
    {
        get
        {
            var roots = new List<string>();
            if (WriteRoot is { } write) roots.Add(write);
            if (AppPaths.RecoveryDirectory is { } standard && !roots.Contains(standard)) roots.Add(standard);
            return roots.Where(Directory.Exists);
        }
    }

    public Task SaveAsync(string sessionId, Document snapshot, int keepVersions, CancellationToken cancellation)
        => Task.Run(() =>
        {
            string root = WriteRoot ?? throw new IOException("There is no recovery folder to write to.");
            string directory = Path.Combine(root, sessionId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, OwnerFile), AppInstance.Id);
            // Claims this copy's lock, which is what marks the owner above as still running.
            _ = AppInstance.Directory;

            string path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}{DocumentFile.Extension}");
            DocumentFile.Save(snapshot, path, cancellation);
            cancellation.ThrowIfCancellationRequested();
            Prune(directory, keepVersions);
        }, cancellation);

    public Task<IReadOnlyList<RecoveryEntry>> FindLeftoversAsync()
    {
        var entries = new List<RecoveryEntry>();
        foreach (string root in SearchRoots)
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string sessionId = Path.GetFileName(directory);
                if (IsInUse(directory, sessionId)) continue;
                string? newest = Versions(directory).FirstOrDefault();
                if (newest is null) continue;
                entries.Add(new RecoveryEntry(sessionId, newest, File.GetLastWriteTimeUtc(newest)));
            }
        }
        IReadOnlyList<RecoveryEntry> ordered = entries.OrderByDescending(e => e.WrittenUtc).ToList();
        return Task.FromResult(ordered);
    }

    public Task<Document> LoadAsync(RecoveryEntry entry)
        => Task.Run(() =>
        {
            // A crash can cut the newest snapshot short; the versions before it are still whole.
            Exception? first = null;
            string directory = Path.GetDirectoryName(entry.Path) ?? "";
            foreach (string path in Versions(directory).Prepend(entry.Path).Distinct())
            {
                try { return DocumentFile.Load(path); }
                catch (Exception ex) { first ??= ex; }
            }
            throw first ?? new FileNotFoundException("The autosaved file is gone.", entry.Path);
        });

    public Task DiscardAsync(string sessionId)
    {
        AutosaveRecovery.MarkInactive(sessionId);
        foreach (string root in SearchRoots)
        {
            string directory = Path.Combine(root, sessionId);
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { /* best-effort: it is offered again next time */ }
        }
        return Task.CompletedTask;
    }

    /// <summary>True while the document these snapshots belong to is open somewhere: in another
    /// running copy of the app, or in this one.</summary>
    private static bool IsInUse(string directory, string sessionId)
    {
        string owner;
        try { owner = File.ReadAllText(Path.Combine(directory, OwnerFile)).Trim(); }
        catch { return false; }   // written by an older version, or unreadable: nobody claims it

        if (owner == AppInstance.Id) return AutosaveRecovery.IsActive(sessionId);
        return AppInstance.IsAlive(owner);
    }

    private static IEnumerable<string> Versions(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*" + DocumentFile.Extension)
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static void Prune(string directory, int keep)
    {
        if (keep <= 0) return;
        foreach (string file in Versions(directory).Skip(keep))
        {
            try { File.Delete(file); } catch { /* best-effort cleanup */ }
        }
    }
}
