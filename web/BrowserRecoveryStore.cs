using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KawaPaint.App.Core;
using KawaPaint.Engine;

namespace KawaPaint.Web;

/// <summary>
/// Browser recovery store: the newest snapshot of each open document, kept in IndexedDB by
/// recovery.js. Without it the web build had no autosave at all, so a closed tab, a reload or a
/// browser crash took the whole drawing with it. The project crosses to JavaScript as base64
/// text, the one form both sides agree on without copying through typed-array views.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class BrowserRecoveryStore : IRecoveryStore
{
    public async Task SaveAsync(string sessionId, Document snapshot, int keepVersions, CancellationToken cancellation)
    {
        // One version per document: browser storage is too tight to keep a history of them.
        string encoded = await Task.Run(() =>
        {
            using var buffer = new MemoryStream();
            DocumentFile.Save(snapshot, buffer, cancellation);
            return Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);
        }, cancellation);
        cancellation.ThrowIfCancellationRequested();
        await Save(sessionId, encoded);
    }

    public async Task<IReadOnlyList<RecoveryEntry>> FindLeftoversAsync()
    {
        string json = await Leftovers() ?? "[]";
        var entries = new List<RecoveryEntry>();
        using var parsed = JsonDocument.Parse(json);
        foreach (JsonElement item in parsed.RootElement.EnumerateArray())
        {
            string? id = item.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id)) continue;
            double written = item.TryGetProperty("written", out JsonElement value) && value.TryGetDouble(out double ms) ? ms : 0;
            entries.Add(new RecoveryEntry(id, id, DateTimeOffset.FromUnixTimeMilliseconds((long)written).UtcDateTime));
        }
        return entries.OrderByDescending(e => e.WrittenUtc).ToList();
    }

    public async Task<Document> LoadAsync(RecoveryEntry entry)
    {
        string encoded = await Load(entry.SessionId)
            ?? throw new FileNotFoundException("The autosaved work is no longer in this browser.");
        return await Task.Run(() =>
        {
            using var buffer = new MemoryStream(Convert.FromBase64String(encoded));
            return DocumentFile.Load(buffer);
        });
    }

    public Task DiscardAsync(string sessionId) => Discard(sessionId);

    /// <summary>Makes the page ask before it is closed or reloaded while there is unsaved work.</summary>
    public static void ReportUnsavedChanges(bool unsaved)
    {
        try { SetUnsaved(unsaved); } catch { /* the warning is a courtesy, never a failure */ }
    }

    /// <summary>Called by recovery.js when the tab is hidden: the app takes a snapshot now.</summary>
    [JSExport]
    internal static void PageHidden() => KawaPaint.App.App.RaiseSentToBackground();

    [JSImport("recovery.save", "main.js")]
    private static partial Task Save(string sessionId, string data);

    [JSImport("recovery.leftovers", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string?> Leftovers();

    [JSImport("recovery.load", "main.js")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string?> Load(string sessionId);

    [JSImport("recovery.discard", "main.js")]
    private static partial Task Discard(string sessionId);

    [JSImport("recovery.setUnsaved", "main.js")]
    private static partial void SetUnsaved(bool unsaved);
}
