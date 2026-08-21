// KawaPaint - the Preferences dialog: the first UI over AppSettings.
//
// AppSettings has had readers since tier 0 (AutosaveService, HistoryStack limits, ConfigGitTracker,
// the plugin hosts) but no way to reach them short of hand-editing settings.json, so every default
// here was effectively frozen. Only settings something actually reads are exposed - a control that
// writes a field nothing consumes is worse than no control at all.
//
// Edits are staged on the controls and written back in one pass on OK, so Cancel really cancels:
// SettingsService.Save raises Changed, which reschedules autosave and can trigger a config commit,
// and neither should fire off a value the user is still typing.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using KawaPaint.App.Core;

namespace KawaPaint.App;

public sealed class SettingsDialog : Window
{
    private readonly SettingsService _settings;

    // Autosave
    private readonly CheckBox _autosaveEnabled = new() { Content = "Save recovery snapshots automatically" };
    private readonly NumericUpDown _autosaveInterval = new() { Minimum = 1, Maximum = 240, Increment = 1 };
    private readonly NumericUpDown _autosaveKeep = new() { Minimum = 1, Maximum = 50, Increment = 1 };
    private readonly CheckBox _autosaveSkipUnchanged = new() { Content = "Skip a snapshot when nothing changed since the last one" };
    private readonly CheckBox _autosaveWriteOriginal = new() { Content = "Also overwrite the file you opened (not just the recovery copy)" };
    private readonly TextBox _recoveryDirectory = new() { PlaceholderText = "Default (beside settings.json)", Width = 210 };

    // History
    private readonly NumericUpDown _historyMaxSteps = new() { Minimum = 0, Maximum = 10000, Increment = 10 };
    private readonly NumericUpDown _historyBudget = new() { Minimum = 16, Maximum = 65536, Increment = 128 };
    private readonly CheckBox _historySpill = new() { Content = "Park steps past the budget on disk instead of dropping them" };

    // Git
    private readonly CheckBox _gitEnabled = new() { Content = "Keep local git history" };
    private readonly CheckBox _gitTrackConfig = new() { Content = "Track this app's own configuration" };
    private readonly CheckBox _gitTrackProjects = new() { Content = "Track linked project folders" };
    private readonly CheckBox _gitCommitOnSave = new() { Content = "Commit on every explicit save" };
    private readonly CheckBox _gitCommitOnAutosave = new() { Content = "Commit on every autosave (noisy)" };
    private readonly NumericUpDown _gitWarnSize = new() { Minimum = 16, Maximum = 65536, Increment = 64 };

    // Plugins
    private readonly CheckBox _pluginsEnabled = new() { Content = "Load KawaPaint plugins" };
    private readonly CheckBox _pdnPluginsEnabled = new() { Content = "Load Paint.NET plugins" };

    public SettingsDialog(SettingsService settings)
    {
        _settings = settings;

        Title = "Preferences";
        Width = 560;
        Height = 560;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Load();

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => { Apply(); Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { ok, cancel }
        };

        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Autosave", Content = Page(AutosavePage()) },
                new TabItem { Header = "History", Content = Page(HistoryPage()) },
                new TabItem { Header = "Git", Content = Page(GitPage()) },
                new TabItem { Header = "Plugins", Content = Page(PluginsPage()) }
            }
        };

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;
    }

    // ---- pages ------------------------------------------------------------

    private static Control Page(StackPanel body) => new ScrollViewer
    {
        Content = body,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Padding = new Thickness(4, 8, 4, 4)
    };

    private StackPanel AutosavePage()
    {
        var browse = new Button { Content = "Browse…" };
        browse.Click += OnBrowseRecoveryDirectory;
        var useDefault = new Button { Content = "Default" };
        useDefault.Click += (_, _) => _recoveryDirectory.Text = "";

        return Stack(
            _autosaveEnabled,
            Row("Snapshot every", _autosaveInterval, "minutes"),
            Row("Keep", _autosaveKeep, "snapshots per document"),
            _autosaveSkipUnchanged,
            _autosaveWriteOriginal,
            Note("Overwriting the opened file makes autosave edit your original unattended. " +
                 "Off by default, and the recovery copy alone is enough to survive a crash."),
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 8, 0, 0),
                Children =
                {
                    new TextBlock { Text = "Recovery folder:", VerticalAlignment = VerticalAlignment.Center },
                    _recoveryDirectory,
                    browse,
                    useDefault
                }
            });
    }

    private StackPanel HistoryPage() => Stack(
        Row("Undo steps to keep", _historyMaxSteps, "(0 = no step limit)"),
        Row("Memory budget", _historyBudget, "MB"),
        _historySpill,
        Note("The budget is a soft ceiling on undo data held in memory. Steps past it either move " +
             "to the on-disk cache or, with that off, are discarded oldest-first."));

    private StackPanel GitPage() => Stack(
        _gitEnabled,
        _gitTrackConfig,
        _gitTrackProjects,
        _gitCommitOnSave,
        _gitCommitOnAutosave,
        Row("Warn once a repository passes", _gitWarnSize, "MB"),
        Note("Local repositories only - nothing is pushed anywhere. Image history grows fast, " +
             "which is what the size warning is for."));

    private StackPanel PluginsPage() => Stack(
        _pluginsEnabled,
        _pdnPluginsEnabled,
        Note("Enable or disable individual plugins, point at a Paint.NET install, and see what " +
             "failed to load under Settings > Manage Plugins. Changes here take effect on the " +
             "next reload from that dialog, or at the next start."),
        SettingsLocationNote());

    private static Control SettingsLocationNote()
    {
        string? root = AppPaths.Root;
        return new TextBlock
        {
            Text = root is null ? "" : "Settings folder: " + root,
            IsVisible = root is not null,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };
    }

    // ---- layout helpers ---------------------------------------------------

    private static StackPanel Stack(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    /// <summary>A labelled numeric field: "&lt;label&gt; [ 15 ] &lt;suffix&gt;".</summary>
    private static Control Row(string label, NumericUpDown field, string suffix)
    {
        field.Width = 110;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
                field,
                new TextBlock { Text = suffix, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 }
            }
        };
    }

    private static Control Note(string text) => new TextBlock
    {
        Text = text,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap
    };

    // ---- load / apply -----------------------------------------------------

    private void Load()
    {
        var s = _settings.Settings;

        _autosaveEnabled.IsChecked = s.Autosave.Enabled;
        _autosaveInterval.Value = s.Autosave.IntervalMinutes;
        _autosaveKeep.Value = s.Autosave.KeepVersions;
        _autosaveSkipUnchanged.IsChecked = s.Autosave.SkipWhenUnchanged;
        _autosaveWriteOriginal.IsChecked = s.Autosave.WriteToOriginalFile;
        _recoveryDirectory.Text = s.Autosave.RecoveryDirectory ?? "";

        _historyMaxSteps.Value = s.History.MaxSteps;
        _historyBudget.Value = s.History.MemoryBudgetMegabytes;
        _historySpill.IsChecked = s.History.SpillToDisk;

        _gitEnabled.IsChecked = s.Git.Enabled;
        _gitTrackConfig.IsChecked = s.Git.TrackConfiguration;
        _gitTrackProjects.IsChecked = s.Git.TrackProjects;
        _gitCommitOnSave.IsChecked = s.Git.CommitOnSave;
        _gitCommitOnAutosave.IsChecked = s.Git.CommitOnAutosave;
        _gitWarnSize.Value = s.Git.RepositoryWarnSizeMegabytes;

        _pluginsEnabled.IsChecked = s.Plugins.Enabled;
        _pdnPluginsEnabled.IsChecked = s.PdnPlugins.Enabled;
    }

    private void Apply()
    {
        _settings.Update(s =>
        {
            s.Autosave.Enabled = _autosaveEnabled.IsChecked ?? true;
            s.Autosave.IntervalMinutes = Int(_autosaveInterval, s.Autosave.IntervalMinutes);
            s.Autosave.KeepVersions = Int(_autosaveKeep, s.Autosave.KeepVersions);
            s.Autosave.SkipWhenUnchanged = _autosaveSkipUnchanged.IsChecked ?? true;
            s.Autosave.WriteToOriginalFile = _autosaveWriteOriginal.IsChecked ?? false;

            string recovery = (_recoveryDirectory.Text ?? "").Trim();
            s.Autosave.RecoveryDirectory = recovery.Length == 0 ? null : recovery;

            s.History.MaxSteps = Int(_historyMaxSteps, s.History.MaxSteps);
            s.History.MemoryBudgetMegabytes = Int(_historyBudget, s.History.MemoryBudgetMegabytes);
            s.History.SpillToDisk = _historySpill.IsChecked ?? true;

            s.Git.Enabled = _gitEnabled.IsChecked ?? false;
            s.Git.TrackConfiguration = _gitTrackConfig.IsChecked ?? true;
            s.Git.TrackProjects = _gitTrackProjects.IsChecked ?? false;
            s.Git.CommitOnSave = _gitCommitOnSave.IsChecked ?? true;
            s.Git.CommitOnAutosave = _gitCommitOnAutosave.IsChecked ?? false;
            s.Git.RepositoryWarnSizeMegabytes = Int(_gitWarnSize, s.Git.RepositoryWarnSizeMegabytes);

            s.Plugins.Enabled = _pluginsEnabled.IsChecked ?? true;
            s.PdnPlugins.Enabled = _pdnPluginsEnabled.IsChecked ?? true;
        });
    }

    /// <summary>NumericUpDown leaves Value null when its box is cleared; keep the stored value
    /// rather than writing a zero the user never asked for.</summary>
    private static int Int(NumericUpDown field, int fallback) =>
        field.Value is { } value ? (int)value : fallback;

    private async void OnBrowseRecoveryDirectory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where autosave recovery snapshots are written",
            AllowMultiple = false
        });

        if (folders.FirstOrDefault()?.Path is { IsAbsoluteUri: true, IsFile: true } uri)
            _recoveryDirectory.Text = uri.LocalPath;
    }
}
