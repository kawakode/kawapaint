using System.Threading;
using System.Threading.Tasks;

namespace KawaPaint.App.Core.Publishing;

public sealed class PublisherAccountService
{
    private readonly ICredentialStore _credentials;
    private readonly PublisherOAuthService _oauth;

    public PublisherAccountService(ICredentialStore? credentials = null, PublisherOAuthService? oauth = null)
    {
        _credentials = credentials ?? PlatformCredentialStore.Instance;
        _oauth = oauth ?? new PublisherOAuthService();
    }

    public bool IsPersistent => _credentials.IsPersistent;

    public ConnectedPublisherAccount? Load(string providerId)
    {
        string? json = _credentials.Read(PublisherCredentialKeys.Account(providerId));
        return json is null ? null : PublisherCredentialJson.Deserialize(json);
    }

    public string? LoadClientSecret(string providerId) =>
        _credentials.Read(PublisherCredentialKeys.ClientSecret(providerId));

    public async Task<ConnectedPublisherAccount> ConnectAsync(string providerId, string clientId,
        string clientSecret, CancellationToken cancellationToken = default)
    {
        ConnectedPublisherAccount account = await _oauth.ConnectAsync(providerId, clientId, clientSecret, cancellationToken);
        Save(providerId, clientSecret, account);
        return account;
    }

    public async Task<ConnectedPublisherAccount> RefreshIfNeededAsync(ConnectedPublisherAccount account,
        string clientId, string clientSecret, CancellationToken cancellationToken = default)
    {
        ConnectedPublisherAccount refreshed = await _oauth.RefreshIfNeededAsync(account, clientId, clientSecret, cancellationToken);
        if (!ReferenceEquals(refreshed, account) && refreshed != account) Save(account.ProviderId, clientSecret, refreshed);
        return refreshed;
    }

    public void Save(string providerId, string clientSecret, ConnectedPublisherAccount account)
    {
        if (string.IsNullOrWhiteSpace(clientSecret)) _credentials.Delete(PublisherCredentialKeys.ClientSecret(providerId));
        else _credentials.Write(PublisherCredentialKeys.ClientSecret(providerId), clientSecret);
        _credentials.Write(PublisherCredentialKeys.Account(providerId), PublisherCredentialJson.Serialize(account));
    }

    public void Disconnect(string providerId)
    {
        _credentials.Delete(PublisherCredentialKeys.Account(providerId));
        _credentials.Delete(PublisherCredentialKeys.ClientSecret(providerId));
    }
}
