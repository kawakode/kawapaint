// KawaPaint — periodic recovery snapshots.
//
// Writes beside the user's own file, never over it: a snapshot lands in a per-session recovery
// folder and the file the user opened is untouched unless WriteToOriginalFile is explicitly on.
// A crash leaves the newest snapshot behind for AutosaveRecovery to find on the next launch.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using KawaPaint.Engine;

namespace KawaPaint.App.Core;

public sealed class AutosaveService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Func<DocumentSession?> _currentSession;
    private DispatcherTimer? _timer;

    public AutosaveService(SettingsService settings, Func<DocumentSession?> currentSession)
    {
        _settings = settings;
        _currentSession = currentSession;
        _settings.Changed += (_, _) => Reschedule();
        Reschedule();
    }

    /// <summary>Raised after a snapshot is written, so the status bar can say so.</summary>
    public event Action<string>? Saved;

    /// <summary>Rebuilds the timer from current settings. Safe to call any time (e.g. after a settings edit).</summary>
    public void Reschedule()
    {
        _timer?.Stop();
        _timer = null;

        var config = _settings.Settings.Autosave;
        if (!config.Enabled || config.IntervalMinutes <= 0) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(config.IntervalMinutes) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        var session = _currentSession();
        if (session is null) return;

        var config = _settings.Settings.Autosave;
        if (config.SkipWhenUnchanged && !session.HasUnsnapshottedChanges) return;

        try
        {
            if (config.WriteToOriginalFile && session.FilePath is not null)
            {
                DocumentFile.Save(session.Document, session.FilePath);
            }
            else
            {
                string? dir = RecoveryDirectoryFor(session, config);
                if (dir is null) return;

                string path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}{DocumentFile.Extension}");
                DocumentFile.Save(session.Document, path);
                Prune(dir, config.KeepVersions);
            }

            session.MarkAutosaved();
            Saved?.Invoke(session.DisplayName);
        }
        catch { /* autosave failing must never interrupt the user's actual work */ }
    }

    private static string? RecoveryDirectoryFor(DocumentSession session, AutosaveSettings config)
    {
        string? root = config.RecoveryDirectory ?? AppPaths.RecoveryDirectory;
        if (root is null) return null;

        string dir = Path.Combine(root, session.SessionId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Prune(string dir, int keep)
    {
        if (keep <= 0) return;

        var files = Directory.EnumerateFiles(dir, "*" + DocumentFile.Extension)
            .OrderByDescending(f => f)
            .Skip(keep);
        foreach (string file in files)
        {
            try { File.Delete(file); } catch { /* best-effort cleanup */ }
        }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }
}

/// <summary>Recovery snapshots left behind by a previous run, offered back to the user at startup.</summary>
public sealed record RecoveryEntry(string SessionId, string Path, DateTime WrittenUtc);

public static class AutosaveRecovery
{
    /// <summary>The newest snapshot per session folder, newest session first. Empty when there is none.</summary>
    public static IReadOnlyList<RecoveryEntry> FindAll()
    {
        string? root = AppPaths.RecoveryDirectory;
        if (root is null || !Directory.Exists(root)) return Array.Empty<RecoveryEntry>();

        var entries = new List<RecoveryEntry>();
        foreach (string sessionDir in Directory.EnumerateDirectories(root))
        {
            string? newest = Directory.EnumerateFiles(sessionDir, "*" + DocumentFile.Extension)
                .OrderByDescending(f => f)
                .FirstOrDefault();
            if (newest is null) continue;

            entries.Add(new RecoveryEntry(Path.GetFileName(sessionDir), newest, File.GetLastWriteTimeUtc(newest)));
        }
        return entries.OrderByDescending(e => e.WrittenUtc).ToList();
    }

    /// <summary>Deletes every recovery snapshot for one session — call once its document is safely saved or discarded.</summary>
    public static void Discard(string sessionId)
    {
        string? root = AppPaths.RecoveryDirectory;
        if (root is null) return;

        string dir = Path.Combine(root, sessionId);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    public static void DiscardAll()
    {
        string? root = AppPaths.RecoveryDirectory;
        if (root is null || !Directory.Exists(root)) return;
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
