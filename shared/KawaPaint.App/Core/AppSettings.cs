// KawaPaint — typed, versioned user preferences.
//
// One tree, serialized as JSON. Every configurable behaviour in the app reads from here rather
// than from its own ad-hoc file, so that enabling git tracking later means tracking one directory.

using System.Collections.Generic;

namespace KawaPaint.App.Core;

public sealed class AppSettings
{
    /// <summary>Bumped whenever a migration is needed; see <see cref="SettingsService.Migrate"/>.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AutosaveSettings Autosave { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public GitSettings Git { get; set; } = new();
    public WorkspaceSettings Workspace { get; set; } = new();
    public PluginSettings Plugins { get; set; } = new();
}

public sealed class AutosaveSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes between automatic recovery snapshots.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>How many recovery snapshots to keep per document before the oldest is dropped.</summary>
    public int KeepVersions { get; set; } = 5;

    /// <summary>
    /// When false (the default) autosave writes only to the recovery directory and never touches
    /// the file the user opened. Turning this on makes autosave overwrite that file in place.
    /// </summary>
    public bool WriteToOriginalFile { get; set; }

    /// <summary>Null means the default recovery directory beside the settings file.</summary>
    public string? RecoveryDirectory { get; set; }

    /// <summary>Skip the snapshot when the document has not changed since the last one.</summary>
    public bool SkipWhenUnchanged { get; set; } = true;
}

public sealed class HistorySettings
{
    /// <summary>Zero means no step limit — the memory budget below is then the only bound.</summary>
    public int MaxSteps { get; set; }

    /// <summary>
    /// Soft ceiling on resident undo data. Steps past the budget spill to disk when
    /// <see cref="SpillToDisk"/> is on, and are otherwise discarded oldest-first.
    /// </summary>
    public int MemoryBudgetMegabytes { get; set; } = 1024;

    public bool SpillToDisk { get; set; } = true;

    /// <summary>Show a per-step thumbnail in the History panel.</summary>
    public bool ShowThumbnails { get; set; } = true;
}

public sealed class GitSettings
{
    /// <summary>Opt-in, and off until the user says otherwise.</summary>
    public bool Enabled { get; set; }

    public bool TrackConfiguration { get; set; } = true;
    public bool TrackProjects { get; set; }

    /// <summary>Commit automatically on every explicit save.</summary>
    public bool CommitOnSave { get; set; } = true;

    /// <summary>Commit automatically on every autosave. Noisy; off by default.</summary>
    public bool CommitOnAutosave { get; set; }

    /// <summary>Warn once the repository grows past this. Image history gets large quickly.</summary>
    public int RepositoryWarnSizeMegabytes { get; set; } = 512;

    /// <summary>Identifier of the configured remote provider ("github", "gitlab", "gitea"), if any.</summary>
    public string? RemoteProvider { get; set; }

    public string? RemoteUrl { get; set; }
}

public sealed class WorkspaceSettings
{
    /// <summary>Name of the layout preset applied at startup.</summary>
    public string ActiveLayout { get; set; } = "Default";

    /// <summary>Saved layout presets, keyed by name.</summary>
    public Dictionary<string, WorkspaceLayout> Layouts { get; set; } = new();

    /// <summary>Command ids pinned to the customizable dock, in display order.</summary>
    public List<string> DockCommands { get; set; } = new();

    /// <summary>Command id to key gesture, overriding the default gesture declared on the command.</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = new();
}

public sealed class PluginSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Extra directories scanned for plugins, on top of the built-in "plugins" folder.</summary>
    public List<string> SearchPaths { get; set; } = new();

    /// <summary>Plugin ids the user has explicitly turned off.</summary>
    public List<string> Disabled { get; set; } = new();
}
