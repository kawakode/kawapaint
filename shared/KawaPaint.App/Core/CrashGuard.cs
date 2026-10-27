// KawaPaint - last line of defence against exceptions nobody caught.
//
// Most UI handlers are async void (clipboard, pickers, dialogs), and an exception escaping one of
// them reaches the dispatcher, which by default takes the whole process down with the user's
// unsaved work. Here the dispatcher-level ones are logged and swallowed instead, and the view is
// told so it can say what went wrong; the document stays open and can still be saved.

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace KawaPaint.App.Core;

public static class CrashGuard
{
    private const int MaxLoggedErrors = 50;   // a handler failing on every frame must not fill the disk
    private static bool _installed;
    private static int _logged;

    /// <summary>Raised on the UI thread for every exception the dispatcher would otherwise have
    /// let terminate the app.</summary>
    public static event Action<Exception>? Reported;

    /// <summary>Hooks the dispatcher and process-wide handlers. Safe to call more than once.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log("ui", e.Exception);
            e.Handled = true;
            try { Reported?.Invoke(e.Exception); }
            catch { /* the reporter itself failing must not undo the rescue */ }
        };

        // A faulted fire-and-forget task: nothing is waiting on it, so observing it is all that
        // is needed to keep it from escalating.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("task", e.Exception);
            e.SetObserved();
        };

        // Exceptions on other threads cannot be stopped from terminating the process, but the
        // log at least tells the user (and us) why it died.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log("fatal", ex);
        };
    }

    /// <summary>Appends to errors.log in the app directory. Silent on platforms without one.</summary>
    public static void Log(string kind, Exception exception)
    {
        Console.Error.WriteLine($"KawaPaint {kind} error: {exception}");
        if (_logged >= MaxLoggedErrors || AppPaths.Root is not { } root) return;
        _logged++;
        try
        {
            File.AppendAllText(Path.Combine(root, "errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging is best-effort */ }
    }
}
