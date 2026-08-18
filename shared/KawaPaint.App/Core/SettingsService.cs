// KawaPaint — loads, migrates and persists AppSettings.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KawaPaint.App.Core;

public sealed class SettingsService
{
    private const string Key = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
}
