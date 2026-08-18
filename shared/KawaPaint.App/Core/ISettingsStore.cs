// KawaPaint — pluggable backing store for persisted app state.
//
// The desktop heads get a real directory; the browser head has no filesystem, so it installs its
// own store (localStorage/OPFS) at startup. Nothing above this interface knows the difference.

using System;
using System.Collections.Generic;
using System.IO;

namespace KawaPaint.App.Core;

/// <summary>A named blob store. Keys are file-name-safe identifiers such as "settings" or "layout".</summary>
public interface ISettingsStore
{
    /// <summary>False when writes are discarded (no persistent storage on this platform).</summary>
    bool CanPersist { get; }

    bool TryRead(string key, out string contents);
    void Write(string key, string contents);
    void Delete(string key);
}

/// <summary>Store of last resort: keeps values for the lifetime of the process only.</summary>
public sealed class MemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public bool CanPersist => false;

    public bool TryRead(string key, out string contents) => _values.TryGetValue(key, out contents!);
    public void Write(string key, string contents) => _values[key] = contents;
    public void Delete(string key) => _values.Remove(key);
}

/// <summary>Desktop store: one file per key under the per-user application data directory.</summary>
public sealed class FileSettingsStore : ISettingsStore
{
    private readonly string _root;

    private FileSettingsStore(string root) => _root = root;

    public bool CanPersist => true;

    /// <summary>Returns null when this platform has no usable application data directory.</summary>
    public static FileSettingsStore? TryCreate(string appName = "KawaPaint")
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) return null;
            string root = Path.Combine(appData, appName);
            Directory.CreateDirectory(root);
            return new FileSettingsStore(root);
        }
        catch { return null; }
    }

    /// <summary>The directory holding every persisted file. Git tracking (when enabled) points here.</summary>
    public string Root => _root;

    private string PathFor(string key) => Path.Combine(_root, key);

    public bool TryRead(string key, out string contents)
    {
        try
        {
            string path = PathFor(key);
            if (File.Exists(path)) { contents = File.ReadAllText(path); return true; }
        }
        catch { /* unreadable is indistinguishable from absent for our purposes */ }
        contents = string.Empty;
        return false;
    }

    public void Write(string key, string contents)
    {
        try
        {
            string path = PathFor(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write-then-replace so a crash mid-write can never leave a truncated settings file.
            string temp = path + ".tmp";
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        catch { /* settings are best-effort; never take down the app over them */ }
    }

    public void Delete(string key)
    {
        try { File.Delete(PathFor(key)); } catch { /* already gone is fine */ }
    }
}

/// <summary>Process-wide store selection. Heads may replace this before the first view is built.</summary>
public static class SettingsStore
{
    private static ISettingsStore? _current;

    public static ISettingsStore Current
    {
        get => _current ??= (ISettingsStore?)FileSettingsStore.TryCreate() ?? new MemorySettingsStore();
        set => _current = value;
    }
}
