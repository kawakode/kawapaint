using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KawaPaint.App.Core;
using KawaPaint.App.Core.Plugins;

namespace KawaPaint.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Whatever is still in the undo spill cache belongs to a previous run whose history died
        // with it, so it is pure waste on disk.
        Core.AppPaths.ClearHistoryCache();

        // Before MainWindow/MainView is constructed, so EffectRegistry/ToolRegistry are already
        // populated by the time RebuildPluginsMenu() runs. A failed plugin is reported, not thrown
        // — see AppPluginHost.LoadAll / PluginManager.
        AppPluginHost.LoadAll(SettingsService.Instance.Settings);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView();
        }

        base.OnFrameworkInitializationCompleted();
    }
}