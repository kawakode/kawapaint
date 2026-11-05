using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using KawaPaint.App.Core;

namespace KawaPaint.Web;

/// <summary>
/// Browser settings store: one localStorage entry per key. Without it the app fell back to a
/// FileSettingsStore on the WebAssembly runtime's in-memory filesystem, which looks persistent
/// but is wiped on every page load - preferences, palette, layouts and export presets were all
/// silently lost on reload.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class LocalStorageSettingsStore : ISettingsStore
{
    private const string Prefix = "kawapaint:";

    private LocalStorageSettingsStore() { }

    public bool CanPersist => true;

    /// <summary>Null when localStorage is unusable (blocked cookies, some private modes).</summary>
    public static LocalStorageSettingsStore? TryCreate()
    {
        try
        {
            const string probe = Prefix + "probe";
            Set(probe, "1");
            bool works = Get(probe) == "1";
            Remove(probe);
            return works ? new LocalStorageSettingsStore() : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("KawaPaint: settings will not persist, localStorage is unavailable: " + ex.Message);
            return null;
        }
    }

    public bool TryRead(string key, out string contents)
    {
        try
        {
            if (Get(Prefix + key) is { } value) { contents = value; return true; }
        }
        catch { /* unreadable is the same as absent */ }
        contents = string.Empty;
        return false;
    }

    // Best effort, like the desktop store: a full quota must never take the app down.
    public void Write(string key, string contents)
    {
        try { Set(Prefix + key, contents); } catch (Exception ex) { Console.Error.WriteLine("KawaPaint: could not save " + key + ": " + ex.Message); }
    }

    public void Delete(string key)
    {
        try { Remove(Prefix + key); } catch { }
    }

    [JSImport("storage.get", "main.js")]
    private static partial string? Get(string key);

    [JSImport("storage.set", "main.js")]
    private static partial void Set(string key, string value);

    [JSImport("storage.remove", "main.js")]
    private static partial void Remove(string key);
}
