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

    // Set for the duration of one autosave (clone through background write). Ticks now return to
    // the UI message loop as soon as the synchronous clone is done (see Tick), so — unlike the old
    // fully-synchronous version, where a single UI thread structurally couldn't fire the timer again
    // mid-save — a slow encode can now genuinely still be running when the next tick lands. This
    // guard is what keeps two encodes from racing on the same recovery folder or WriteToOriginalFile
    // path.
    private bool _saving;

    private bool _disposed;

    public AutosaveService(SettingsService settings, Func<DocumentSession?> currentSession)
    {
        _settings = settings;
        _currentSession = currentSession;
        // Named handler (not a lambda) so Dispose can actually detach it. SettingsService.Instance
        // is a process-lifetime singleton, so a lambda here outlived this service and any later
        // settings save would call Reschedule() on a disposed autosaver — which builds and starts a
        // BRAND NEW timer, resurrecting the very thing Dispose was meant to stop. Same shape as the
        // static-registry-event fix in MainView.
        _settings.Changed += OnSettingsChanged;
        Reschedule();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Reschedule();

    /// <summary>Raised after a snapshot is written, so the status bar can say so.</summary>
    public event Action<string>? Saved;

    /// <summary>Rebuilds the timer from current settings. Safe to call any time (e.g. after a settings edit).</summary>
    public void Reschedule()
    {
        _timer?.Stop();
        _timer = null;
        if (_disposed) return;   // belt and braces: never re-arm after Dispose, whoever calls this

        var config = _settings.Settings.Autosave;
        if (!config.Enabled || config.IntervalMinutes <= 0) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(config.IntervalMinutes) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>
    /// The actual zip+PNG encode used to run inline on this DispatcherTimer callback, freezing the
    /// UI thread for as long as a big document takes to write. Now only the layer clone — a plain
    /// memcpy, not an encode — runs synchronously; that's still what keeps the snapshot torn-free
    /// (the live Document could otherwise be mutated by further painting while the slow part below
    /// runs), and it's fast enough not to be felt. Everything slow (path/dir resolution, the actual
    /// encode, pruning old versions) happens in the background Task.
    /// </summary>
    private async void Tick()
    {
        if (_saving) return;   // a previous autosave is still writing; this tick sits out

        var session = _currentSession();
        if (session is null) return;

        var config = _settings.Settings.Autosave;
        if (config.SkipWhenUnchanged && !session.HasUnsnapshottedChanges) return;

        _saving = true;
        try
        {
            using var snapshot = session.Document.Clone();

            bool wrote = await System.Threading.Tasks.Task.Run(() =>
            {
                if (config.WriteToOriginalFile && session.FilePath is not null)
                {
                    DocumentFile.Save(snapshot, session.FilePath);
                    return true;
                }

                string? dir = RecoveryDirectoryFor(session, config);
                if (dir is null) return false;

                string path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}{DocumentFile.Extension}");
                DocumentFile.Save(snapshot, path);
                Prune(dir, config.KeepVersions);
                return true;
            });

            if (wrote)
            {
                session.MarkAutosaved();
                Saved?.Invoke(session.DisplayName);
            }
        }
        catch { /* autosave failing must never interrupt the user's actual work */ }
        finally { _saving = false; }
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

    /// <summary>
    /// Stops the timer and detaches from settings changes. Known accepted gap, unchanged here: an
    /// already-in-flight background save is not cancelled (see Tick) — it may complete after this
    /// returns, which is harmless but real.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
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
