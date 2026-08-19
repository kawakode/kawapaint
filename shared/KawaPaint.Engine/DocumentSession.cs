// KawaPaint — the single source of truth for "which document, from where, and has it changed".
//
// Autosave, crash recovery, git commits and the close-confirm prompt all have to agree on the
// answers; before this existed each one tracked its own flag.

using System;

namespace KawaPaint.Engine;

/// <summary>Why the document is considered changed, for the autosave and commit messages.</summary>
public enum DirtyReason
{
    None,
    Edit,
    Structure,
    Metadata
}

public sealed class DocumentSession
{
    private bool _dirty;

    public DocumentSession(Document document, string? filePath = null, string? displayName = null)
    {
        Document = document;
        FilePath = filePath;
        DisplayName = displayName ?? DeriveName(filePath);
    }

    /// <summary>Stable for the lifetime of this document, and the name of its recovery folder.</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];

    public Document Document { get; private set; }

    /// <summary>Full path when the document came from or was written to a real file, else null.</summary>
    public string? FilePath { get; private set; }

    /// <summary>
    /// Directory this document mirrors into as the exploded (git-diffable) format, or null if it
    /// isn't linked to one. Independent of <see cref="FilePath"/> — the primary .kwp save is
    /// unaffected either way; this is purely an additional target the App layer writes to and
    /// commits, per <c>GitSettings.TrackProjects</c>.
    /// </summary>
    public string? GitProjectDirectory { get; private set; }

    public void SetGitProjectDirectory(string? directoryPath)
    {
        GitProjectDirectory = directoryPath;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shown in the title bar. Falls back to "untitled" for a document with no file.</summary>
    public string DisplayName { get; private set; }

    public bool IsDirty => _dirty;
    public DirtyReason Reason { get; private set; } = DirtyReason.None;

    public DateTime? LastSavedUtc { get; private set; }
    public DateTime? LastAutosavedUtc { get; private set; }

    /// <summary>
    /// Edit counter at the last save. Autosave compares against <see cref="EditCount"/> to skip
    /// snapshots for a document that has not moved since the previous one.
    /// </summary>
    public long EditCount { get; private set; }
    public long EditCountAtLastSnapshot { get; private set; }

    public bool HasUnsnapshottedChanges => EditCount != EditCountAtLastSnapshot;

    public event EventHandler? Changed;

    public void MarkDirty(DirtyReason reason = DirtyReason.Edit)
    {
        EditCount++;
        _dirty = true;
        Reason = reason;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records an explicit save. Clears the dirty flag and adopts the new path, if given.</summary>
    public void MarkSaved(string? filePath = null, string? displayName = null)
    {
        if (filePath is not null) FilePath = filePath;
        if (displayName is not null) DisplayName = displayName;
        else if (filePath is not null) DisplayName = DeriveName(filePath);

        _dirty = false;
        Reason = DirtyReason.None;
        LastSavedUtc = DateTime.UtcNow;
        EditCountAtLastSnapshot = EditCount;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Records a recovery snapshot. Deliberately does not clear the dirty flag: the user's own
    /// file has not been written, so closing still has to prompt.
    /// </summary>
    public void MarkAutosaved()
    {
        LastAutosavedUtc = DateTime.UtcNow;
        EditCountAtLastSnapshot = EditCount;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Swaps in a replacement document (crop, resize, rotate, flatten) without losing identity.</summary>
    public Document Replace(Document document)
    {
        var old = Document;
        Document = document;
        MarkDirty(DirtyReason.Structure);
        return old;
    }

    /// <summary>Adopts a freshly opened or created document, resetting all save state.</summary>
    public void Reset(Document document, string? filePath, string? displayName = null)
    {
        Document = document;
        FilePath = filePath;
        DisplayName = displayName ?? DeriveName(filePath);
        GitProjectDirectory = null;
        _dirty = false;
        Reason = DirtyReason.None;
        LastSavedUtc = null;
        LastAutosavedUtc = null;
        EditCount = 0;
        EditCountAtLastSnapshot = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string DeriveName(string? filePath)
        => string.IsNullOrEmpty(filePath) ? "untitled" : System.IO.Path.GetFileName(filePath);
}
