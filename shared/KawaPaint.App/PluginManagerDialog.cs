// KawaPaint — lists plugin load results and lets the user enable/disable and reload. Styled like
// AboutDialog/RecoveryDialog (plain Window/StackPanel) since no Settings/Preferences dialog exists
// anywhere in this app yet to extend instead.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using KawaPaint.App.Core;
using KawaPaint.App.Core.Plugins;
using KawaPaint.Engine.Plugins;

namespace KawaPaint.App;

public sealed class PluginManagerDialog : Window
{
    private readonly SettingsService _settings;
    private readonly Action _onReload;
    private readonly StackPanel _list = new() { Spacing = 6 };

    public PluginManagerDialog(SettingsService settings, Action onReload)
    {
        _settings = settings;
        _onReload = onReload;

        Title = "Manage Plugins";
        Width = 480;
        Height = 420;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var scroller = new ScrollViewer
        {
            Content = _list,
            Height = 320,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var reload = new Button { Content = "Reload Plugins" };
        reload.Click += (_, _) => { AppPluginHost.Reload(_settings.Settings); _onReload(); Rebuild(); };

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        var buttons = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        buttons.Children.Add(reload);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children = { scroller, buttons }
        };

        Rebuild();
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
        if (result.Version is not null) title += $" — v{result.Version}";
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
}
