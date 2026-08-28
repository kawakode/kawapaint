using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KawaPaint.App.Core.Publishing;

public sealed class PublisherOAuthService
{
    private readonly HttpClient _http;

    public PublisherOAuthService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new HttpClientHandler
        { AutomaticDecompression = System.Net.DecompressionMethods.All });
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("KawaPaint/1.0");
        if (!_http.DefaultRequestHeaders.AcceptEncoding.Any()) _http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
    }

    public Task<ConnectedPublisherAccount> ConnectAsync(string providerId, string clientId,
        string clientSecret, CancellationToken cancellationToken = default) => providerId switch
    {
        "tumblr" => ConnectTumblrAsync(clientId, clientSecret, cancellationToken),
        "deviantart" => ConnectDeviantArtAsync(clientId, clientSecret, cancellationToken),
        "facebook" => ConnectFacebookAsync(clientId, clientSecret, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(providerId), "Unknown publisher.")
    };

    public async Task<ConnectedPublisherAccount> RefreshIfNeededAsync(ConnectedPublisherAccount account,
        string clientId, string clientSecret, CancellationToken cancellationToken = default)
    {
        if (account.Token.ExpiresAt is null || account.Token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return account;
        if (string.IsNullOrWhiteSpace(account.Token.RefreshToken))
            throw new InvalidOperationException($"The {account.ProviderId} connection expired. Connect it again.");

        string endpoint = account.ProviderId switch
        {
            "tumblr" => "https://api.tumblr.com/v2/oauth2/token",
            "deviantart" => "https://www.deviantart.com/oauth2/token",
            _ => throw new InvalidOperationException("The Facebook connection expired. Connect it again.")
        };
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = account.Token.RefreshToken,
            ["client_id"] = clientId
        };
        if (!string.IsNullOrWhiteSpace(clientSecret)) fields["client_secret"] = clientSecret;
        OAuthToken token = await ExchangeTokenAsync(endpoint, fields, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
            token = token with { RefreshToken = account.Token.RefreshToken };
        return account with { Token = token };
    }

    private async Task<ConnectedPublisherAccount> ConnectTumblrAsync(string clientId, string clientSecret,
        CancellationToken cancellationToken)
    {
        string code = await LoopbackOAuthReceiver.AuthorizeAsync(state => BuildUri("https://www.tumblr.com/oauth2/authorize",
            new Dictionary<string, string> { ["client_id"] = clientId, ["response_type"] = "code", ["scope"] = "basic write offline_access",
                ["state"] = state, ["redirect_uri"] = LoopbackOAuthReceiver.RedirectUri.AbsoluteUri }), cancellationToken);
        OAuthToken token = await ExchangeTokenAsync("https://api.tumblr.com/v2/oauth2/token", new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["client_secret"] = clientSecret, ["redirect_uri"] = LoopbackOAuthReceiver.RedirectUri.AbsoluteUri
        }, cancellationToken).ConfigureAwait(false);

        using JsonDocument info = await GetJsonAsync("https://api.tumblr.com/v2/user/info", token.AccessToken, cancellationToken);
        JsonElement user = info.RootElement.GetProperty("response").GetProperty("user");
        var targets = new List<PublisherTarget>();
        foreach (JsonElement blog in user.GetProperty("blogs").EnumerateArray())
        {
            string id = blog.TryGetProperty("uuid", out var uuid) ? uuid.GetString()! : blog.GetProperty("name").GetString()!;
            targets.Add(new PublisherTarget(id, blog.GetProperty("name").GetString()!));
        }
        return new ConnectedPublisherAccount("tumblr", token, targets,
            user.TryGetProperty("name", out var name) ? name.GetString() : null);
    }

    private async Task<ConnectedPublisherAccount> ConnectDeviantArtAsync(string clientId, string clientSecret,
        CancellationToken cancellationToken)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string code = await LoopbackOAuthReceiver.AuthorizeAsync(state => BuildUri("https://www.deviantart.com/oauth2/authorize",
            new Dictionary<string, string> { ["client_id"] = clientId, ["response_type"] = "code", ["scope"] = "basic stash publish",
                ["state"] = state, ["redirect_uri"] = LoopbackOAuthReceiver.RedirectUri.AbsoluteUri,
                ["code_challenge"] = challenge, ["code_challenge_method"] = "S256" }), cancellationToken);
        var tokenFields = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["redirect_uri"] = LoopbackOAuthReceiver.RedirectUri.AbsoluteUri, ["code_verifier"] = verifier
        };
        if (!string.IsNullOrWhiteSpace(clientSecret)) tokenFields["client_secret"] = clientSecret;
        OAuthToken token = await ExchangeTokenAsync("https://www.deviantart.com/oauth2/token",
            tokenFields, cancellationToken).ConfigureAwait(false);
        using JsonDocument who = await GetJsonAsync("https://www.deviantart.com/api/v1/oauth2/user/whoami", token.AccessToken, cancellationToken);
        string name = who.RootElement.TryGetProperty("username", out var username) ? username.GetString() ?? "DeviantArt" : "DeviantArt";
        return new ConnectedPublisherAccount("deviantart", token, [new PublisherTarget("me", name)], name);
    }

    private async Task<ConnectedPublisherAccount> ConnectFacebookAsync(string clientId, string clientSecret,
        CancellationToken cancellationToken)
    {
        const string graph = "v24.0";
        string code = await LoopbackOAuthReceiver.AuthorizeAsync(state => BuildUri($"https://www.facebook.com/{graph}/dialog/oauth",
            new Dictionary<string, string> { ["client_id"] = clientId, ["response_type"] = "code", ["scope"] = "pages_show_list,pages_manage_posts",
                ["state"] = state, ["redirect_uri"] = LoopbackOAuthReceiver.RedirectUri.AbsoluteUri }), cancellationToken);
        OAuthToken token = await ExchangeTokenAsync($"https://graph.facebook.com/{graph}/oauth/access_token", new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
            ["client_secret"] = clientSecret, ["redirect_uri"] = LoopbackOAuthReceiver.RedirectUri.AbsoluteUri
        }, cancellationToken).ConfigureAwait(false);
        using JsonDocument pages = await GetJsonAsync($"https://graph.facebook.com/{graph}/me/accounts?fields=id,name,access_token,tasks",
            token.AccessToken, cancellationToken);
        var targets = new List<PublisherTarget>();
        foreach (JsonElement page in pages.RootElement.GetProperty("data").EnumerateArray())
            targets.Add(new PublisherTarget(page.GetProperty("id").GetString()!, page.GetProperty("name").GetString()!,
                page.GetProperty("access_token").GetString()));
        return new ConnectedPublisherAccount("facebook", token, targets);
    }

    private async Task<OAuthToken> ExchangeTokenAsync(string endpoint,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(endpoint, new FormUrlEncodedContent(fields), cancellationToken).ConfigureAwait(false);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw OAuthError(endpoint, response, body);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        string access = root.GetProperty("access_token").GetString()!;
        string? refresh = root.TryGetProperty("refresh_token", out var refreshNode) ? refreshNode.GetString() : null;
        DateTimeOffset? expires = root.TryGetProperty("expires_in", out var expiresNode) && expiresNode.TryGetInt32(out int seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null;
        string type = root.TryGetProperty("token_type", out var typeNode) ? typeNode.GetString() ?? "bearer" : "bearer";
        return new OAuthToken(access, refresh, expires, type);
    }

    private async Task<JsonDocument> GetJsonAsync(string uri, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw OAuthError(uri, response, body);
        return JsonDocument.Parse(body);
    }

    private static Exception OAuthError(string operation, HttpResponseMessage response, byte[] body)
    {
        string detail;
        try
        {
            using var json = JsonDocument.Parse(body);
            JsonElement root = json.RootElement;
            detail = root.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" :
                root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m)
                    ? m.GetString() ?? "" : root.GetRawText();
        }
        catch { detail = System.Text.Encoding.UTF8.GetString(body); }
        return new InvalidOperationException($"OAuth request failed ({(int)response.StatusCode}) at {operation}: {detail}");
    }

    private static Uri BuildUri(string endpoint, IReadOnlyDictionary<string, string> query) =>
        new(endpoint + "?" + string.Join('&', query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value))));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
