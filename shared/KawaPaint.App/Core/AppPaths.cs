// KawaPaint - where the app keeps its own files.
//
// All of it hangs off the settings store's directory, so turning on git tracking means tracking
// one location. Scratch directories live under it too but are excluded from tracking and cleared
// at startup, since they only ever hold data belonging to a process that is no longer running.

using System;
using System.IO;

namespace KawaPaint.App.Core;

public static class AppPaths
{
    /// <summary>Null on platforms with no filesystem (the browser build).</summary>
    public static string? Root => (SettingsStore.Current as FileSettingsStore)?.Root;

    /// <summary>Autosave snapshots, kept until the user recovers or discards them.</summary>
    public static string? RecoveryDirectory => Combine("recovery");

    /// <summary>Undo steps parked on disk. Scratch: nothing here survives the process, and
    /// each running copy of the app has its own directory (see <see cref="AppInstance"/>).</summary>
    public static string? HistorySpillDirectory => AppInstance.Directory;

    /// <summary>Layout presets, palettes and other exportable user data.</summary>
    public static string? PresetsDirectory => Combine("presets");

    /// <summary>Built-in plugin search location. One subdirectory per plugin, named after its
    /// declared Id. Null on the browser build, same as every other path here.</summary>
    public static string? PluginsDirectory => Combine("plugins");

    /// <summary>Real, unmodified third-party Paint.NET classic-tier plugin DLLs - a flat folder,
    /// one loose *.dll per plugin (unlike PluginsDirectory's one-folder-per-plugin convention),
    /// matching how these DLLs are actually distributed. See PdnEffectDiscovery.</summary>
    public static string? PdnPluginsDirectory => Combine("pdn-plugins");

    private static string? Combine(string name)
    {
        string? root = Root;
        if (root is null) return null;

        string path = Path.Combine(root, name);
        try { Directory.CreateDirectory(path); }
        catch { return null; }
        return path;
    }

    /// <summary>
    /// Clears the undo spill caches of copies of the app that are no longer running. Called at
    /// startup: those files belong to a run whose history is gone, so they are pure waste. A copy
    /// that is still running keeps its cache - deleting it broke that copy's Undo.
    /// </summary>
    public static void ClearHistoryCache() => AppInstance.RemoveStaleScratch();
}
