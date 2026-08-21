// KawaPaint - git-backs the app's own config directory (settings.json, layout presets) when
// GitSettings.TrackConfiguration is on. This is the first real reader of AppSettings.Git: separate
// from project tracking (below), and independent of it -- a user can back up their configuration
// without ever linking a project, or vice versa.

using System;

namespace KawaPaint.App.Core;

public sealed class ConfigGitTracker : IDisposable
{
    private readonly SettingsService _settings;
    private bool _repoEnsured;

    public ConfigGitTracker(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
        TryCommit(); // covers first run: establishes the repo even before anything changes again
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => TryCommit();

    private void TryCommit()
    {
        var git = _settings.Settings.Git;
        if (!git.Enabled || !git.TrackConfiguration) return;

        string? root = AppPaths.Root;
        if (root is null) return; // no filesystem (browser build)

        if (!_repoEnsured)
        {
            if (!GitService.EnsureRepository(root, out _)) return;
            // recovery/ holds timestamped binary snapshots and history-cache/ is pure scratch,
            // cleared at every startup (see AppPaths) -- neither belongs in a tracked history.
            GitService.EnsureGitIgnore(root, "recovery/", "history-cache/");
            _repoEnsured = true;
        }

        GitService.CommitAll(root, "Update configuration", out _);
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
