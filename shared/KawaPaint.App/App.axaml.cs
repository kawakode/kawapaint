using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KawaPaint.App.Core;
using KawaPaint.App.Core.Plugins;
using KawaPaint.App.Core.Plugins.Pdn;

namespace KawaPaint.App;

public partial class App : Application
{
    /// <summary>
    /// Leaves the app on a host where it cannot just close a window - Android sets it to finish
    /// the activity. Null elsewhere, and MainView only asks for it after confirming.
    /// </summary>
    public static Action? QuitApp { get; set; }

    /// <summary>
    /// Told whenever the open document starts or stops having unsaved changes. The browser head
    /// uses it to make the page ask before it is closed or reloaded. Null elsewhere.
    /// </summary>
    public static Action<bool>? UnsavedChangesChanged { get; set; }

    /// <summary>
    /// A file named on the command line, opened once the first view is up. This is how the
    /// system's "Open with" and a file dropped on the app's icon arrive on Windows and Linux.
    /// </summary>
    public static string? StartupFile { get; set; }

    /// <summary>
    /// Raised by a head that knows the app just left the foreground in a way the framework does
    /// not report (a browser tab being hidden). The view takes a recovery snapshot.
    /// </summary>
    public static event Action? SentToBackground;

    public static void RaiseSentToBackground() => SentToBackground?.Invoke();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        CrashGuard.Install();

        // Undo spill files of a previous run whose history died with it are pure waste on disk.
        // Another copy of the app that is still running keeps its own.
        Core.AppPaths.ClearHistoryCache();

        // Android uses an activity factory and may recreate its activity. Desktop plugin discovery
        // is deliberately skipped there: loading arbitrary assemblies is neither mobile-safe nor
        // trimming-safe. Built-in tools and effects remain available on every platform.
        bool isAndroid = ApplicationLifetime is IActivityApplicationLifetime;
        if (!isAndroid)
        {
            // Before MainWindow/MainView is constructed, so EffectRegistry/ToolRegistry are already
            // populated by the time RebuildPluginsMenu() runs. A failed plugin is reported, not thrown
            // - see AppPluginHost.LoadAll / PluginManager.
            AppPluginHost.LoadAll(SettingsService.Instance.Settings);
            AppPdnPluginHost.LoadAll(SettingsService.Instance.Settings);
        }

        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new MainView();
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupFile = FirstExistingFile(desktop.Args);
            desktop.MainWindow = new MainWindow();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? FirstExistingFile(string[]? args)
    {
        if (args is null) return null;
        foreach (string arg in args)
        {
            if (arg.StartsWith('-')) continue;
            try { if (System.IO.File.Exists(arg)) return System.IO.Path.GetFullPath(arg); }
            catch { /* not a path */ }
        }
        return null;
    }
}
