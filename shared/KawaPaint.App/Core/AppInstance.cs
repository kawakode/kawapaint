// KawaPaint - tells one running copy of the app from another.
//
// Several windows of KawaPaint can run at once, and they share one settings directory. Scratch
// data (the undo spill cache, recovery snapshots) used to be treated as "left over from a crash"
// by whichever copy started last, which then deleted the files a still-running copy depended on.
// Each copy now owns a scratch directory holding a lock file it keeps open for its whole life;
// a directory whose lock can be taken belongs to a process that is gone.

using System;
using System.IO;
using System.Linq;

namespace KawaPaint.App.Core;

public static class AppInstance
{
    private const string ScratchFolder = "history-cache";   // already excluded from git tracking
    private const string LockName = ".lock";

    private static readonly object Gate = new();
    private static FileStream? _lock;
    private static string? _directory;

    /// <summary>Names this process among the copies sharing the settings directory.</summary>
    public static string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Parent of every copy's scratch directory. Null without a filesystem.</summary>
    public static string? ScratchRoot => AppPaths.Root is { } root ? Path.Combine(root, ScratchFolder) : null;

    /// <summary>
    /// This copy's own scratch directory, created and locked on first use. Null on platforms
    /// without a filesystem, or when the directory cannot be claimed.
    /// </summary>
    public static string? Directory
    {
        get
        {
            if (ScratchRoot is not { } root) return null;
            lock (Gate)
            {
                string directory = Path.Combine(root, Id);
                if (_lock is not null && _directory == directory && System.IO.Directory.Exists(directory))
                    return directory;
                try
                {
                    _lock?.Dispose();
                    _lock = null;
                    System.IO.Directory.CreateDirectory(directory);
                    _lock = new FileStream(Path.Combine(directory, LockName),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    _directory = directory;
                    return directory;
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// True for this process and for any other copy still holding its lock. An id nobody ever
    /// claimed, or whose owner has exited or crashed, is not alive.
    /// </summary>
    public static bool IsAlive(string? id)
    {
        if (!IsWellFormed(id)) return false;
        if (id == Id) return true;
        if (ScratchRoot is not { } root) return false;

        string path = Path.Combine(root, id!, LockName);
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;   // nobody holds it any more
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }                    // held by its owner
        catch (UnauthorizedAccessException) { return true; }    // cannot tell: never delete on a guess
    }

    /// <summary>
    /// Removes the scratch directories of copies that are no longer running, plus the loose
    /// files older versions wrote straight into the scratch root. Called once at startup.
    /// </summary>
    public static void RemoveStaleScratch()
    {
        if (ScratchRoot is not { } root || !System.IO.Directory.Exists(root)) return;
        try
        {
            foreach (string file in System.IO.Directory.EnumerateFiles(root, "*.hst"))
            {
                try { File.Delete(file); } catch { /* locked: not worth failing startup over */ }
            }
            foreach (string directory in System.IO.Directory.EnumerateDirectories(root))
            {
                if (IsAlive(Path.GetFileName(directory))) continue;
                try { System.IO.Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }
            }
        }
        catch { /* an unreadable scratch root is not worth failing startup over */ }
    }

    // Ids are read back from files on disk, so they must never be able to walk out of the root.
    private static bool IsWellFormed(string? id)
        => id is { Length: > 0 and <= 64 } && id.All(char.IsAsciiLetterOrDigit);
}
