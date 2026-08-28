using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KawaPaint.App.Core;
using KawaPaint.App.Core.Publishing;
using KawaPaint.Engine.Exporting;
using KawaPaint.Engine.Publishing;

namespace KawaPaint.App;

public sealed record PublishArtworkSelection(
    string ProviderId,
    string PresetName,
    ConnectedPublisherAccount Account,
    PublisherTarget Target,
    string Title,
    string Caption,
    string AltText,
    IReadOnlyList<string> Tags,
    PublishState State,
    bool IsMature,
    string? MatureLevel,
    IReadOnlyList<string> MatureClassifications,
    string? GalleryId,
    bool IsAiGenerated,
    bool NoAi);

public sealed class PublishArtworkDialog : Window
{
    private sealed record Choice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly SettingsService _settings;
    private readonly PublisherAccountService _accounts;
    private readonly ComboBox _provider = new() { Width = 220 };
    private readonly ComboBox _preset = new() { Width = 260 };
    private readonly TextBox _clientId = new() { MinWidth = 280 };
    private readonly TextBox _clientSecret = new() { MinWidth = 280, PasswordChar = '●' };
    private readonly Button _connect = new() { Content = "Connect account…" };
    private readonly Button _disconnect = new() { Content = "Disconnect" };
    private readonly TextBlock _connection = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly ComboBox _target = new() { MinWidth = 280 };
    private readonly TextBox _title = new();
    private readonly TextBox _caption = new() { AcceptsReturn = true, MinHeight = 90, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _altText = new() { AcceptsReturn = true, MinHeight = 55, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _tags = new() { PlaceholderText = "art, illustration, landscape" };
    private readonly ComboBox _state = new() { ItemsSource = new[] { "Publish now", "Draft", "Queue" } };
    private readonly CheckBox _mature = new() { Content = "Mature content" };
    private readonly ComboBox _matureLevel = new() { ItemsSource = new[] { "moderate", "strict" } };
    private readonly TextBox _matureClasses = new() { PlaceholderText = "nudity, sexual, gore, language, ideology" };
    private readonly TextBox _galleryId = new() { PlaceholderText = "Optional DeviantArt gallery UUID" };
    private readonly CheckBox _aiGenerated = new() { Content = "AI-generated artwork" };
    private readonly CheckBox _noAi = new() { Content = "Disallow use in third-party AI datasets", IsChecked = true };
    private readonly Button _publish = new() { Content = "Continue to publish", IsDefault = true };
    private ConnectedPublisherAccount? _account;
    private bool _busy;

    public PublishArtworkSelection? Selection { get; private set; }

    public PublishArtworkDialog(SettingsService settings, string initialTitle,
        PublisherAccountService? accountService = null)
    {
        _settings = settings;
        _accounts = accountService ?? new PublisherAccountService();
        Title = "Publish Artwork";
        Width = 690;
        Height = 760;
        MinWidth = 600;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _provider.ItemsSource = new[]
        {
            new Choice("tumblr", "Tumblr"), new Choice("deviantart", "DeviantArt"),
            new Choice("facebook", "Facebook Page")
        };
        _preset.ItemsSource = settings.Settings.ExportPresets.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        _title.Text = initialTitle;
        _state.SelectedIndex = 0;
        _matureLevel.SelectedIndex = 0;

        _provider.SelectionChanged += (_, _) => LoadProvider();
        _connect.Click += async (_, _) => await ConnectAsync();
        _disconnect.Click += (_, _) => Disconnect();
        _mature.IsCheckedChanged += (_, _) => UpdateProviderFields();
        _publish.Click += async (_, _) => await AcceptAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);

        var credentials = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 10, RowSpacing = 8
        };
        int row = 0;
        AddRow(credentials, row++, "Platform", _provider);
        AddRow(credentials, row++, "Export preset", _preset);
        AddRow(credentials, row++, "Client / app ID", _clientId);
        AddRow(credentials, row++, "Client secret", _clientSecret);
        AddRow(credentials, row++, "Account", Buttons(_connect, _disconnect));
        AddRow(credentials, row++, "", _connection);
        AddRow(credentials, row++, "Destination", _target);
        AddRow(credentials, row++, "Title", _title);
        AddRow(credentials, row++, "Caption / description", _caption);
        AddRow(credentials, row++, "Alt text", _altText);
        AddRow(credentials, row++, "Tags", _tags);
        AddRow(credentials, row++, "Post state", _state);
        AddRow(credentials, row++, "DeviantArt options", new StackPanel
        {
            Spacing = 6, Children = { _mature, _matureLevel, _matureClasses, _galleryId, _aiGenerated, _noAi }
        });

        var note = new TextBlock
        {
            Text = $"Register each API application with redirect URI {LoopbackOAuthReceiver.RedirectUri}. " +
                   "Secrets and OAuth tokens are kept out of settings.json and projects. " +
                   (_accounts.IsPersistent ? "This system has a supported credential vault." :
                       "No supported credential vault is available, so credentials last only for this process."),
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 10, 0, 0)
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Children = { cancel, _publish }
        };
        var content = new StackPanel { Spacing = 8, Children = { credentials, note } };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 12, Margin = new Thickness(16) };
        var scroll = new ScrollViewer { Content = content };
        Grid.SetRow(scroll, 0); Grid.SetRow(buttons, 1);
        root.Children.Add(scroll); root.Children.Add(buttons);
        Content = root;

        string wantedProvider = settings.Settings.Publishing.LastProviderId ?? "tumblr";
        _provider.SelectedItem = _provider.ItemsSource.Cast<Choice>().First(c => c.Id == wantedProvider);
        string? wantedPreset = settings.Settings.Publishing.LastExportPreset;
        _preset.SelectedItem = wantedPreset is not null && settings.Settings.ExportPresets.ContainsKey(wantedPreset)
            ? wantedPreset : settings.Settings.ExportPresets.Keys.FirstOrDefault();
    }

    private string ProviderId => (_provider.SelectedItem as Choice)?.Id ?? "tumblr";

    private void LoadProvider()
    {
        string id = ProviderId;
        PublishingSettings p = _settings.Settings.Publishing;
        _clientId.Text = id switch
        {
            "tumblr" => p.TumblrClientId,
            "deviantart" => p.DeviantArtClientId,
            "facebook" => p.FacebookAppId,
            _ => null
        };
        _clientSecret.Text = "";
        _clientSecret.PlaceholderText = _accounts.LoadClientSecret(id) is null
            ? (id == "deviantart" ? "Optional for a public PKCE client" : "Required to connect")
            : "Stored securely — leave blank to reuse";
        _account = _accounts.Load(id);
        PopulateTargets();
        UpdateProviderFields();
    }

    private void PopulateTargets()
    {
        _target.ItemsSource = _account?.Targets ?? Array.Empty<PublisherTarget>();
        if (_account is null)
        {
            _connection.Text = "Not connected";
            _target.SelectedItem = null;
            return;
        }
        _connection.Text = "Connected" + (string.IsNullOrWhiteSpace(_account.AccountName) ? "" : " as " + _account.AccountName);
        string? wanted = ProviderId switch
        {
            "tumblr" => _settings.Settings.Publishing.TumblrBlogId,
            "facebook" => _settings.Settings.Publishing.FacebookPageId,
            _ => "me"
        };
        _target.SelectedItem = _account.Targets.FirstOrDefault(t => t.Id == wanted) ?? _account.Targets.FirstOrDefault();
    }

    private void UpdateProviderFields()
    {
        bool tumblr = ProviderId == "tumblr";
        bool deviant = ProviderId == "deviantart";
        _state.IsEnabled = tumblr;
        if (!tumblr) _state.SelectedIndex = 0;
        _mature.IsVisible = deviant;
        _matureLevel.IsVisible = deviant && _mature.IsChecked == true;
        _matureClasses.IsVisible = deviant && _mature.IsChecked == true;
        _galleryId.IsVisible = deviant;
        _aiGenerated.IsVisible = deviant;
        _noAi.IsVisible = deviant;
        _title.PlaceholderText = deviant ? "Required" : "Optional";
        _disconnect.IsEnabled = _account is not null && !_busy;
    }

    private async System.Threading.Tasks.Task ConnectAsync()
    {
        if (_busy) return;
        string clientId = _clientId.Text?.Trim() ?? "";
        string secret = string.IsNullOrEmpty(_clientSecret.Text)
            ? _accounts.LoadClientSecret(ProviderId) ?? "" : _clientSecret.Text;
        if (clientId.Length == 0 || (secret.Length == 0 && ProviderId != "deviantart"))
        {
            _connection.Text = ProviderId == "deviantart" ? "Client ID is required." :
                "Client/app ID and client secret are required.";
            return;
        }

        SetBusy(true, "Waiting for authorization in your browser…");
        try
        {
            _account = await _accounts.ConnectAsync(ProviderId, clientId, secret);
            SaveClientId(clientId);
            _clientSecret.Text = "";
            _clientSecret.PlaceholderText = secret.Length == 0
                ? "Optional for a public PKCE client" : "Stored securely — leave blank to reuse";
            PopulateTargets();
        }
        catch (Exception ex) { _connection.Text = "Connection failed: " + ex.Message; }
        finally { SetBusy(false); UpdateProviderFields(); }
    }

    private void Disconnect()
    {
        _accounts.Disconnect(ProviderId);
        _account = null;
        PopulateTargets();
        UpdateProviderFields();
    }

    private async System.Threading.Tasks.Task AcceptAsync()
    {
        if (_busy) return;
        if (_preset.SelectedItem is not string presetName)
        {
            _connection.Text = "Choose an export preset.";
            return;
        }
        if (_account is null || _target.SelectedItem is not PublisherTarget target)
        {
            _connection.Text = "Connect an account and choose a destination.";
            return;
        }
        string clientId = _clientId.Text?.Trim() ?? "";
        string secret = string.IsNullOrEmpty(_clientSecret.Text)
            ? _accounts.LoadClientSecret(ProviderId) ?? "" : _clientSecret.Text;
        try
        {
            SetBusy(true, "Checking the account connection…");
            _account = await _accounts.RefreshIfNeededAsync(_account, clientId, secret);
            target = _account.Targets.FirstOrDefault(t => t.Id == target.Id) ?? target;
        }
        catch (Exception ex)
        {
            _connection.Text = ex.Message;
            SetBusy(false);
            return;
        }

        PublishState state = _state.SelectedIndex switch
        {
            1 => PublishState.Draft,
            2 => PublishState.Queue,
            _ => PublishState.Published
        };
        string[] classifications = Split(_matureClasses.Text);
        Selection = new PublishArtworkSelection(ProviderId, presetName, _account, target,
            _title.Text?.Trim() ?? "", _caption.Text?.Trim() ?? "", _altText.Text?.Trim() ?? "",
            Split(_tags.Text), state, _mature.IsChecked == true,
            _mature.IsChecked == true ? _matureLevel.SelectedItem as string : null,
            classifications, NullIfEmpty(_galleryId.Text), _aiGenerated.IsChecked == true,
            _noAi.IsChecked == true);
        SaveSelection(target, presetName);
        Close(true);
    }

    private void SaveClientId(string clientId) => _settings.Update(s =>
    {
        if (ProviderId == "tumblr") s.Publishing.TumblrClientId = clientId;
        else if (ProviderId == "deviantart") s.Publishing.DeviantArtClientId = clientId;
        else s.Publishing.FacebookAppId = clientId;
    });

    private void SaveSelection(PublisherTarget target, string presetName) => _settings.Update(s =>
    {
        SaveClientIdWithoutSaving(s.Publishing, ProviderId, _clientId.Text?.Trim());
        s.Publishing.LastProviderId = ProviderId;
        s.Publishing.LastExportPreset = presetName;
        if (ProviderId == "tumblr") { s.Publishing.TumblrBlogId = target.Id; s.Publishing.TumblrBlogName = target.Name; }
        if (ProviderId == "facebook") { s.Publishing.FacebookPageId = target.Id; s.Publishing.FacebookPageName = target.Name; }
    });

    private static void SaveClientIdWithoutSaving(PublishingSettings settings, string providerId, string? value)
    {
        if (providerId == "tumblr") settings.TumblrClientId = value;
        else if (providerId == "deviantart") settings.DeviantArtClientId = value;
        else settings.FacebookAppId = value;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        _connect.IsEnabled = !busy;
        _disconnect.IsEnabled = !busy && _account is not null;
        _publish.IsEnabled = !busy;
        _provider.IsEnabled = !busy;
        if (message is not null) _connection.Text = message;
    }

    private static string[] Split(string? value) => (value ?? "")
        .Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => x.TrimStart('#')).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StackPanel Buttons(params Control[] controls)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (Control control in controls) panel.Children.Add(control);
        return panel;
    }

    private static void AddRow(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row); Grid.SetColumn(text, 0);
        Grid.SetRow(control, row); Grid.SetColumn(control, 1);
        grid.Children.Add(text); grid.Children.Add(control);
    }
}
