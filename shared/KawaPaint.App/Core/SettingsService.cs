// KawaPaint — loads, migrates and persists AppSettings.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KawaPaint.App.Core;

public sealed class SettingsService
{
    private const string Key = "settings.json";

    // AllowNamedFloatingPointLiterals: PanelPlacement's dock/float sizes default to NaN ("size
    // to content"), which System.Text.Json otherwise refuses to write at all — every save with a
    // panel still at its default size would silently no-op without this.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly ISettingsStore _store;
    private bool _saveQueued;

    public SettingsService(ISettingsStore store)
    {
        _store = store;
        Settings = Load(store);
    }

    /// <summary>The shared instance. Heads may set <see cref="SettingsStore.Current"/> first.</summary>
    public static SettingsService Instance { get; } = new(SettingsStore.Current);

    public AppSettings Settings { get; private set; }

    /// <summary>Raised after any successful save, so open views can re-read what changed.</summary>
    public event EventHandler? Changed;

    private static AppSettings Load(ISettingsStore store)
    {
        try
        {
            if (store.TryRead(Key, out string json))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null) return Migrate(loaded);
            }
        }
        catch { /* a corrupt settings file must not stop the app from starting */ }
        return new AppSettings();
    }

    /// <summary>
    /// Brings an older settings tree up to <see cref="AppSettings.CurrentSchemaVersion"/>. Each
    /// version bump adds one step here; unknown newer versions are left alone and used as-is.
    /// </summary>
    private static AppSettings Migrate(AppSettings settings)
    {
        // No migrations yet — version 1 is the first shipped schema.
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return settings;
    }

    /// <summary>Applies a change and persists it.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        mutate(Settings);
        Save();
    }

    public void Save()
    {
        try
        {
            _store.Write(Key, JsonSerializer.Serialize(Settings, JsonOptions));
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Coalesces bursts of writes (panel drags emit one per pointer move) into a single save on
    /// the next dispatcher pass.
    /// </summary>
    public void SaveDeferred()
    {
        if (_saveQueued) return;
        _saveQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _saveQueued = false;
            Save();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void Reset()
    {
        Settings = new AppSettings();
        Save();
    }

    private const int MaxRecentFiles = 10;

    /// <summary>Moves <paramref name="path"/> to the front of the recent-files list, deduping and
    /// capping it, and persists. Call on every successful project open or save.</summary>
    public void AddRecentFile(string path)
    {
        var recent = Settings.RecentFiles;
        recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, path);
        while (recent.Count > MaxRecentFiles) recent.RemoveAt(recent.Count - 1);
        Save();
    }

    /// <summary>Drops a path that turned out to be missing/unopenable.</summary>
    public void RemoveRecentFile(string path)
    {
        if (Settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
            Save();
    }
}
