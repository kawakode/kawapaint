using KawaPaint.App.Core;
using KawaPaint.Engine;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>
/// Recovery snapshots and the undo spill cache when more than one copy of the app shares the
/// settings directory, and when a snapshot cannot be read back. Before these, a second window
/// deleted the first one's undo cache and autosaves, and a failed restore deleted the snapshot.
/// </summary>
public sealed class RecoveryStoreTests : IDisposable
{
    private readonly ISettingsStore _previousStore = SettingsStore.Current;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kawa_recovery_" + Guid.NewGuid().ToString("N"));
    private readonly FileRecoveryStore _store = new();
    private readonly List<IDisposable> _held = new();

    public RecoveryStoreTests() => SettingsStore.Current = FileSettingsStore.Create(_root);

    public void Dispose()
    {
        foreach (var held in _held) held.Dispose();
        SettingsStore.Current = _previousStore;
        try { Directory.Delete(_root, recursive: true); } catch { /* this process still holds its own lock */ }
    }

    private static Document NewDocument()
    {
        var document = new Document(8, 6);
        document.AddLayer("Background");
        return document;
    }

    private async Task<string> SaveSnapshotAsync(string sessionId)
    {
        using var document = NewDocument();
        await _store.SaveAsync(sessionId, document, keepVersions: 5, TestContext.Current.CancellationToken);
        return Path.Combine(AppPaths.RecoveryDirectory!, sessionId);
    }

    /// <summary>Makes a snapshot folder look like it was written by another copy of the app.</summary>
    private string GiveToOtherInstance(string sessionDirectory, bool running)
    {
        string other = Guid.NewGuid().ToString("N")[..12];
        File.WriteAllText(Path.Combine(sessionDirectory, "owner"), other);
        if (running) _held.Add(HoldLock(other));
        return other;
    }

    private static FileStream HoldLock(string instanceId)
    {
        string directory = Path.Combine(AppInstance.ScratchRoot!, instanceId);
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task SnapshotOfACrashedCopyIsOfferedBack()
    {
        string directory = await SaveSnapshotAsync("crashed");
        GiveToOtherInstance(directory, running: false);

        var leftovers = await _store.FindLeftoversAsync();

        var entry = Assert.Single(leftovers);
        Assert.Equal("crashed", entry.SessionId);
        using var restored = await _store.LoadAsync(entry);
        Assert.Equal(8, restored.Width);
    }

    [Fact]
    public async Task SnapshotOfACopyStillRunningIsLeftAlone()
    {
        string directory = await SaveSnapshotAsync("elsewhere");
        GiveToOtherInstance(directory, running: true);

        Assert.Empty(await _store.FindLeftoversAsync());

        // Once that copy is gone its work becomes recoverable.
        foreach (var held in _held) held.Dispose();
        _held.Clear();
        Assert.Single(await _store.FindLeftoversAsync());
    }

    [Fact]
    public async Task OpenDocumentOfThisCopyIsNotALeftover()
    {
        await SaveSnapshotAsync("mine");
        AutosaveRecovery.MarkActive("mine");
        try
        {
            Assert.Empty(await _store.FindLeftoversAsync());
        }
        finally
        {
            AutosaveRecovery.MarkInactive("mine");
        }

        // The view that owned it was torn down without saving (Android recreating its activity).
        Assert.Single(await _store.FindLeftoversAsync());
    }

    [Fact]
    public async Task UnreadableNewestSnapshotFallsBackToTheOneBefore()
    {
        string directory = await SaveSnapshotAsync("torn");
        GiveToOtherInstance(directory, running: false);
        string torn = Path.Combine(directory, "99991231-235959" + DocumentFile.Extension);
        File.WriteAllText(torn, "cut short by the crash");

        var entry = Assert.Single(await _store.FindLeftoversAsync());
        Assert.Equal(torn, entry.Path);

        using var restored = await _store.LoadAsync(entry);
        Assert.Equal(6, restored.Height);
    }

    [Fact]
    public async Task FailedRestoreKeepsTheSnapshot()
    {
        string directory = Path.Combine(AppPaths.RecoveryDirectory!, "broken");
        Directory.CreateDirectory(directory);
        string only = Path.Combine(directory, "20260101-000000" + DocumentFile.Extension);
        File.WriteAllText(only, "not a project");

        var entry = Assert.Single(await _store.FindLeftoversAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => _store.LoadAsync(entry));

        Assert.True(File.Exists(only));
        Assert.Single(await _store.FindLeftoversAsync());

        await _store.DiscardAsync("broken");
        Assert.Empty(await _store.FindLeftoversAsync());
    }

    [Fact]
    public void StartupKeepsTheUndoCacheOfACopyStillRunning()
    {
        string scratch = AppInstance.ScratchRoot!;
        string running = Guid.NewGuid().ToString("N")[..12];
        string gone = Guid.NewGuid().ToString("N")[..12];
        _held.Add(HoldLock(running));
        HoldLock(gone).Dispose();
        string runningStep = Path.Combine(scratch, running, "step.hst");
        string goneStep = Path.Combine(scratch, gone, "step.hst");
        string looseStep = Path.Combine(scratch, "old-version.hst");
        File.WriteAllText(runningStep, "undo");
        File.WriteAllText(goneStep, "undo");
        File.WriteAllText(looseStep, "undo");
        string? own = AppPaths.HistorySpillDirectory;

        AppPaths.ClearHistoryCache();

        Assert.True(File.Exists(runningStep));
        Assert.False(Directory.Exists(Path.Combine(scratch, gone)));
        Assert.False(File.Exists(looseStep));
        Assert.NotNull(own);
        Assert.True(Directory.Exists(own));
        Assert.True(AppInstance.IsAlive(running));
        Assert.False(AppInstance.IsAlive(gone));
        Assert.False(AppInstance.IsAlive("../" + running));
    }
}
