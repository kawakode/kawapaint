// KawaPaint - lists plugin load results and lets the user enable/disable and reload. Styled like
// AboutDialog/RecoveryDialog (plain Window/StackPanel) since no Settings/Preferences dialog exists
// anywhere in this app yet to extend instead.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using KawaPaint.App.Core;
using KawaPaint.App.Core.Plugins;
using KawaPaint.App.Core.Plugins.Pdn;
using KawaPaint.Engine.Plugins;

namespace KawaPaint.App;

public sealed class PluginManagerDialog : Window
{
    private readonly SettingsService _settings;
    private readonly Action _onReload;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly StackPanel _pdnList = new() { Spacing = 6 };
    private readonly TextBox _pdnPathBox = new() { PlaceholderText = "Auto-detected", IsReadOnly = true };
    private readonly TextBlock _pdnStatusText = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    public PluginManagerDialog(SettingsService settings, Action onReload)
    {
        _settings = settings;
        _onReload = onReload;

        Title = "Manage Plugins";
        Width = 480;
        Height = 620;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var scroller = new ScrollViewer
        {
            Content = _list,
            Height = 220,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var browse = new Button { Content = "Browse…" };
        browse.Click += OnBrowsePdnInstall;
        var autoDetect = new Button { Content = "Auto-detect" };
        autoDetect.Click += (_, _) => SetPdnInstallOverride(null);
        var pdnPathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        pdnPathRow.Children.Add(new TextBlock { Text = "Paint.NET install:", VerticalAlignment = VerticalAlignment.Center });
        pdnPathRow.Children.Add(_pdnPathBox);
        pdnPathRow.Children.Add(browse);
        pdnPathRow.Children.Add(autoDetect);
        _pdnPathBox.Width = 180;

        var pdnScroller = new ScrollViewer
        {
            Content = _pdnList,
            Height = 200,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var reload = new Button { Content = "Reload Plugins" };
        reload.Click += (_, _) => { AppPdnPluginHost.Reload(_settings.Settings); _onReload(); Rebuild(); RebuildPdn(); };

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        var buttons = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        buttons.Children.Add(reload);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Native Plugins", FontWeight = FontWeight.Bold },
                scroller,
                new TextBlock { Text = "Paint.NET Plugins", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 0) },
                pdnPathRow,
                _pdnStatusText,
                pdnScroller,
                buttons
            }
        };

        Rebuild();
        RebuildPdn();
    }

    private async void OnBrowsePdnInstall(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the folder containing your paint.net install (PaintDotNet.Effects.Core.dll)",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        string? path = folder?.Path is { IsAbsoluteUri: true, IsFile: true } uri ? uri.LocalPath : null;
        if (path is null) return;

        SetPdnInstallOverride(path);
    }

    private void SetPdnInstallOverride(string? path)
    {
        _settings.Update(s => s.PdnPlugins.InstallDirectoryOverride = path);
        AppPdnPluginHost.Reload(_settings.Settings);
        _onReload();
        Rebuild();
        RebuildPdn();
    }

    private void Rebuild()
    {
        _list.Children.Clear();

        if (AppPluginHost.LastResults.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "No plugins found.", Opacity = 0.7 });
            return;
        }

        foreach (var result in AppPluginHost.LastResults)
            _list.Children.Add(BuildRow(result));
    }

    private Control BuildRow(PluginLoadResult result)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var enabled = new CheckBox
        {
            IsChecked = result.Status != PluginStatus.Disabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        enabled.IsCheckedChanged += (_, _) => SetDisabled(result.Id, enabled.IsChecked != true);
        header.Children.Add(enabled);

        string title = result.Name is null ? result.Id : $"{result.Name} ({result.Id})";
        if (result.Version is not null) title += $" - v{result.Version}";
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });

        header.Children.Add(new TextBlock
        {
            Text = result.Status.ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = result.Status switch
            {
                PluginStatus.Loaded => Brushes.LightGreen,
                PluginStatus.Failed => Brushes.OrangeRed,
                _ => Brushes.Gray
            }
        });

        var block = new StackPanel { Spacing = 2, Children = { header } };
        if (result.Error is not null)
            block.Children.Add(new TextBlock { Text = result.Error, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(28, 0, 0, 0) });

        return block;
    }

    private void SetDisabled(string id, bool disabled)
    {
        _settings.Update(s =>
        {
            s.Plugins.Disabled.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            if (disabled) s.Plugins.Disabled.Add(id);
        });
    }

    private void RebuildPdn()
    {
        _pdnPathBox.Text = _settings.Settings.PdnPlugins.InstallDirectoryOverride;

        _pdnStatusText.Text = AppPdnPluginHost.InstallInfo is { } info
            ? $"Detected paint.net {info.Version} at {info.InstallDirectory}"
            : "Paint.NET not found - install it (e.g. the official portable ZIP from github.com/paintdotnet/release) or set a folder above.";

        _pdnList.Children.Clear();

        if (AppPdnPluginHost.LastResults.Count == 0)
        {
            _pdnList.Children.Add(new TextBlock { Text = "No Paint.NET plugins found.", Opacity = 0.7 });
            return;
        }

        foreach (var result in AppPdnPluginHost.LastResults)
            _pdnList.Children.Add(BuildPdnRow(result));
    }

    private Control BuildPdnRow(PluginLoadResult result)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var enabled = new CheckBox
        {
            IsChecked = result.Status != PluginStatus.Disabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        enabled.IsCheckedChanged += (_, _) => SetPdnDisabled(result.Id, enabled.IsChecked != true);
        header.Children.Add(enabled);

        header.Children.Add(new TextBlock
        {
            Text = result.Name ?? result.Id,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });

        header.Children.Add(new TextBlock
        {
            Text = result.Status.ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = result.Status switch
            {
                PluginStatus.Loaded => Brushes.LightGreen,
                PluginStatus.Failed => Brushes.OrangeRed,
                _ => Brushes.Gray
            }
        });

        var block = new StackPanel { Spacing = 2, Children = { header } };
        if (result.Error is not null)
            block.Children.Add(new TextBlock { Text = result.Error, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(28, 0, 0, 0) });

        return block;
    }

    private void SetPdnDisabled(string id, bool disabled)
    {
        _settings.Update(s =>
        {
            s.PdnPlugins.Disabled.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            if (disabled) s.PdnPlugins.Disabled.Add(id);
        });
    }
}
