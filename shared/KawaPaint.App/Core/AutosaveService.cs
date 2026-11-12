// KawaPaint - periodic recovery snapshots.
//
// Writes beside the user's own file, never over it: a snapshot lands in a per-session recovery
// folder and the file the user opened is untouched unless WriteToOriginalFile is explicitly on.
// A crash leaves the newest snapshot behind for the next launch to find (see RecoveryStore).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using KawaPaint.Engine;

namespace KawaPaint.App.Core;

public sealed class AutosaveService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Func<DocumentSession?> _currentSession;
    private DispatcherTimer? _timer;

    // Set for the duration of one autosave (clone through background write). A slow encode can
    // still be running when the next tick lands; this guard is what keeps two encodes from racing
    // on the same recovery folder or WriteToOriginalFile path.
    private bool _saving;
    private CancellationTokenSource? _saveCancellation;
    private bool _disposed;
    private bool _failureReported;

    // Sessions this service has written snapshots for. They stop counting as "in use" when the
    // service is disposed, so a view that is torn down and rebuilt in the same process (Android
    // recreating its activity) is offered the work of the one before it.
    private readonly HashSet<string> _ownedSessions = new(StringComparer.Ordinal);

    public AutosaveService(SettingsService settings, Func<DocumentSession?> currentSession)
    {
        _settings = settings;
        _currentSession = currentSession;
        // Named handler (not a lambda) so Dispose can actually detach it. SettingsService.Instance
        // is a process-lifetime singleton, so a lambda here outlived this service and any later
        // settings save would call Reschedule() on a disposed autosaver - which builds and starts a
        // BRAND NEW timer, resurrecting the very thing Dispose was meant to stop.
        _settings.Changed += OnSettingsChanged;
        Reschedule();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Reschedule();

    /// <summary>Raised after a snapshot is written, so the status bar can say so.</summary>
    public event Action<string>? Saved;

    /// <summary>Raised with the reason when a snapshot could not be written. Once per run of
    /// failures, so a full disk does not rewrite the status bar every few minutes.</summary>
    public event Action<string>? Failed;

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

    private async void Tick() => await SaveAsync(onlyWhenChanged: _settings.Settings.Autosave.SkipWhenUnchanged);

    /// <summary>
    /// Takes a snapshot right now instead of waiting for the timer - when the app is sent to the
    /// background (a phone may never bring it back) and right after a recovery. Does nothing when
    /// autosave is off or the document has not changed since its last snapshot. Completes with
    /// true once a snapshot has been written.
    /// </summary>
    public Task<bool> SaveNowAsync() => SaveAsync(onlyWhenChanged: true);

    // Only the layer clone - a plain memcpy, not an encode - runs synchronously on the UI thread;
    // that is what keeps the snapshot torn-free while painting continues. Everything slow (the
    // encode, pruning old versions) happens off the UI thread.
    private async Task<bool> SaveAsync(bool onlyWhenChanged)
    {
        if (_saving || _disposed) return false;   // a previous autosave is still writing; this one sits out
        var session = _currentSession();
        if (session is null) return false;

        var config = _settings.Settings.Autosave;
        if (!config.Enabled) return false;
        if (onlyWhenChanged && !session.HasUnsnapshottedChanges) return false;

        _saving = true;
        var cancellation = new CancellationTokenSource();
        _saveCancellation = cancellation;
        try
        {
            using var snapshot = session.Document.Clone();
            if (config.WriteToOriginalFile && session.FilePath is { } original)
            {
                await Task.Run(() => DocumentFile.Save(snapshot, original, cancellation.Token));
            }
            else
            {
                if (RecoveryStore.Current is not { } store) return false;
                AutosaveRecovery.MarkActive(session.SessionId);
                lock (_ownedSessions) _ownedSessions.Add(session.SessionId);
                await store.SaveAsync(session.SessionId, snapshot, config.KeepVersions, cancellation.Token);
            }

            if (_disposed || cancellation.IsCancellationRequested) return false;
            session.MarkAutosaved();
            _failureReported = false;
            Saved?.Invoke(session.DisplayName);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Autosave failing must never interrupt the user's actual work, but it must not stay
            // secret either: with a full disk the user would otherwise believe they are covered.
            CrashGuard.Log("autosave", ex);
            if (!_disposed && !_failureReported)
            {
                _failureReported = true;
                try { Failed?.Invoke(ex.Message); } catch { /* a status bar must not undo the rescue */ }
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(_saveCancellation, cancellation)) _saveCancellation = null;
            cancellation.Dispose();
            _saving = false;
        }
    }

    /// <summary>
    /// Stops the timer, detaches from settings changes, and cancels an in-flight snapshot before
    /// its atomic temp-file move. A native PNG encode already running may take until that layer
    /// returns to observe cancellation, but no cancelled autosave is published afterwards.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _timer?.Stop();
        _timer = null;
        _saveCancellation?.Cancel();
        lock (_ownedSessions)
        {
            foreach (string id in _ownedSessions) AutosaveRecovery.MarkInactive(id);
            _ownedSessions.Clear();
        }
    }
}

/// <summary>
/// Which documents of this process are still open. A snapshot belonging to one of them is live
/// work, not something to offer back as "recovered".
/// </summary>
public static class AutosaveRecovery
{
    private static readonly HashSet<string> Active = new(StringComparer.Ordinal);

    public static void MarkActive(string sessionId) { lock (Active) Active.Add(sessionId); }
    public static void MarkInactive(string sessionId) { lock (Active) Active.Remove(sessionId); }
    public static bool IsActive(string sessionId) { lock (Active) return Active.Contains(sessionId); }
}
