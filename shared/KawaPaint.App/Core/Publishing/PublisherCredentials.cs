using System;
using System.Collections.Generic;
using System.Text.Json;

namespace KawaPaint.App.Core.Publishing;

public sealed record OAuthToken(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string TokenType = "bearer");

public sealed record PublisherTarget(string Id, string Name, string? AccessToken = null);

public sealed record ConnectedPublisherAccount(
    string ProviderId,
    OAuthToken Token,
    IReadOnlyList<PublisherTarget> Targets,
    string? AccountName = null);

internal static class PublisherCredentialKeys
{
    public static string Account(string providerId) => "publisher." + providerId + ".account";
    public static string ClientSecret(string providerId) => "publisher." + providerId + ".client-secret";
}

internal static class PublisherCredentialJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize(ConnectedPublisherAccount account) => JsonSerializer.Serialize(account, Options);
    public static ConnectedPublisherAccount? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<ConnectedPublisherAccount>(json, Options); }
        catch { return null; }
    }
}
