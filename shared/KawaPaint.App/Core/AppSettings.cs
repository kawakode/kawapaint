// KawaPaint - typed, versioned user preferences.
//
// One tree, serialized as JSON. Every configurable behaviour in the app reads from here rather
// than from its own ad-hoc file, so that enabling git tracking later means tracking one directory.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using KawaPaint.Engine;
using KawaPaint.Engine.Codecs;
using KawaPaint.Engine.Exporting;

namespace KawaPaint.App.Core;

public sealed class AppSettings
{
    /// <summary>Bumped whenever a migration is needed; see <see cref="SettingsService.Migrate"/>.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AutosaveSettings Autosave { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public GitSettings Git { get; set; } = new();
    public WorkspaceSettings Workspace { get; set; } = new();
    public PluginSettings Plugins { get; set; } = new();
    public PdnPluginSettings PdnPlugins { get; set; } = new();

    /// <summary>Named, reusable image-export recipes shown under File > Export.</summary>
    public Dictionary<string, ExportPreset> ExportPresets { get; set; } = CreateDefaultExportPresets();

    /// <summary>Most-recently-opened project paths, newest first. See SettingsService.AddRecentFile.</summary>
    public List<string> RecentFiles { get; set; } = new();

    public static Dictionary<string, ExportPreset> CreateDefaultExportPresets() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Web PNG"] = new()
        {
            CodecId = "png", ResizeMode = ExportResizeMode.FitWithin,
            Width = 2048, Height = 2048, FilenamePattern = "{name}-web.{ext}"
        },
        ["Art Square"] = new()
        {
            CodecId = "jpeg", EncodeOptions = new EncodeOptions { Quality = 92 },
            ResizeMode = ExportResizeMode.FitAndPad, Width = 1080, Height = 1080,
            PaddingColor = "FFFFFFFF", FilenamePattern = "{name}-square.{ext}",
            PackageText = "{name}\n\n#art", CopyPackageTextToClipboard = true
        },
        ["Art Portrait 4x5"] = new()
        {
            CodecId = "jpeg", EncodeOptions = new EncodeOptions { Quality = 92 },
            ResizeMode = ExportResizeMode.FitAndPad, Width = 1080, Height = 1350,
            PaddingColor = "FFFFFFFF", FilenamePattern = "{name}-portrait.{ext}",
            PackageText = "{name}\n\n#art", CopyPackageTextToClipboard = true
        },
        ["Art Landscape 16x9"] = new()
        {
            CodecId = "jpeg", EncodeOptions = new EncodeOptions { Quality = 92 },
            ResizeMode = ExportResizeMode.FitAndPad, Width = 1920, Height = 1080,
            PaddingColor = "FFFFFFFF", FilenamePattern = "{name}-landscape.{ext}",
            PackageText = "{name}\n\n#art", CopyPackageTextToClipboard = true
        }
    };
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
    /// <summary>Zero means no step limit - the memory budget below is then the only bound.</summary>
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

    public bool ShowRulers { get; set; } = true;

    /// <summary>Show composited thumbnails in timeline frame cards.</summary>
    public bool ShowFramePreviews { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter<RulerUnit>))]
    public RulerUnit RulerUnit { get; set; } = RulerUnit.Pixels;

    /// <summary>Desktop window geometry from the last run; null until the app has been closed
    /// once, and always null on the browser build, which has no window to restore.</summary>
    public WindowGeometry? Window { get; set; }
}

/// <summary>
/// Where the desktop window was, in screen pixels. Panel placement has been persisted since the
/// docking framework landed, but the window holding it opened at a fixed 1100x720 every time,
/// so a layout arranged for a maximized window came back squeezed into a small one.
/// </summary>
public sealed class WindowGeometry
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>X/Y/Width/Height are the *restored* geometry even when this is true, so
    /// un-maximizing after a restore lands where the window used to be rather than at a default.</summary>
    public bool Maximized { get; set; }
}

public sealed class PluginSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Extra directories scanned for plugins, on top of the built-in "plugins" folder.</summary>
    public List<string> SearchPaths { get; set; } = new();

    /// <summary>Plugin ids the user has explicitly turned off.</summary>
    public List<string> Disabled { get; set; } = new();
}

/// <summary>Real, unmodified third-party Paint.NET classic-tier (Effect/PropertyBasedEffect)
/// plugin DLLs. KawaPaint never bundles paint.net's own assemblies (see FORK.TXT for why this
/// fork exists at all) - this only points at a real paint.net install the user already has
/// separately on their own machine. See KawaPaint.Engine.Plugins.Pdn.PdnEffectDiscovery.</summary>
public sealed class PdnPluginSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Null means auto-detect via PdnInstallLocator's well-known paths. Set when the
    /// user pointed this at a portable-ZIP extraction or a non-standard install location.</summary>
    public string? InstallDirectoryOverride { get; set; }

    /// <summary>Extra directories scanned for loose plugin DLLs, on top of the built-in
    /// "pdn-plugins" folder.</summary>
    public List<string> SearchPaths { get; set; } = new();

    /// <summary>Disabled entries, keyed "&lt;dll file name&gt;::&lt;effect type full name&gt;".</summary>
    public List<string> Disabled { get; set; } = new();
}
