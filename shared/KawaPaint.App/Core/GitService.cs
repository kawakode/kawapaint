// KawaPaint - thin wrapper around LibGit2Sharp for the two things "git-backed history" needs:
// make sure a directory is a repo, and commit whatever changed in it. Nothing here ever pushes,
// pulls, or touches a remote - that's forge integration, a separate and not-yet-built piece (see
// TODO.md's 2.3 entry). This only ever runs against a local working directory the user already
// controls (their config folder, or a project folder they explicitly linked), so init-on-first-use
// is safe: it's the local equivalent of the user having typed `git init` themselves.
//
// Every public method swallows its own failures (a missing git config identity, a locked file, a
// half-written repo) and reports them back as a bool/message rather than throwing, matching
// AutosaveService's "must never interrupt the user's actual work" rule -- committing history is a
// convenience layered on top of a save that already succeeded, not a precondition for it.

using System;
using System.IO;
using LibGit2Sharp;

namespace KawaPaint.App.Core;

public static class GitService
{
    /// <summary>Creates <paramref name="directoryPath"/> and inits a git repo there if either is missing. No-op if already a repo.</summary>
    public static bool EnsureRepository(string directoryPath, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(directoryPath);
            if (!Repository.IsValid(directoryPath))
                Repository.Init(directoryPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Stages every change under <paramref name="directoryPath"/> and commits it. Returns false
    /// with no error set (not a failure) when there was nothing to commit -- a save that produced
    /// byte-identical files (nothing actually changed) is the common case, not an edge case.
    /// </summary>
    public static bool CommitAll(string directoryPath, string message, out string? error)
    {
        error = null;
        try
        {
            using var repo = new Repository(directoryPath);
            Commands.Stage(repo, "*");

            var status = repo.RetrieveStatus();
            if (!status.IsDirty) return false;

            var author = BuildSignature(repo);
            repo.Commit(message, author, author);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Writes a .gitignore listing <paramref name="patterns"/> if the file doesn't already exist.
    /// Never overwrites one the user (or a previous run) already put there.
    /// </summary>
    public static void EnsureGitIgnore(string directoryPath, params string[] patterns)
    {
        string path = Path.Combine(directoryPath, ".gitignore");
        if (File.Exists(path)) return;
        try { File.WriteAllLines(path, patterns); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Prefers the user's own configured git identity (global/system ~/.gitconfig) so commits look
    /// like theirs in `git log`; falls back to a fixed identity only when none is configured, since
    /// BuildSignature throws rather than degrading on its own.
    /// </summary>
    private static Signature BuildSignature(Repository repo)
    {
        try { return repo.Config.BuildSignature(DateTimeOffset.Now); }
        catch (LibGit2SharpException)
        {
            return new Signature("KawaPaint", "kawapaint@localhost", DateTimeOffset.Now);
        }
    }
}
