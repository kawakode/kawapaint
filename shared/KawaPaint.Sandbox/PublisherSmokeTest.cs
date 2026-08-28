using System.Net;
using System.Net.Http.Headers;
using System.Text;
using KawaPaint.App.Core;
using KawaPaint.App.Core.Publishing;
using KawaPaint.Engine.Publishing;

namespace KawaPaint.Sandbox;

internal static class PublisherSmokeTest
{
    public static void RunAll()
    {
        DefaultInstagramPresets();
        SettingsMigrationAddsInstagramPresets();
        CredentialsStayOutOfSettings();
        RefreshKeepsExistingTokenForPublicClient().GetAwaiter().GetResult();
        TumblrRequest().GetAwaiter().GetResult();
        FacebookRequest().GetAwaiter().GetResult();
        DeviantArtTwoStageRequest().GetAwaiter().GetResult();
        DeviantArtRetainsStashOnFailure().GetAwaiter().GetResult();
        AmbiguousTransportFailureIsNotRetryable().GetAwaiter().GetResult();
        Console.WriteLine("PUBLISHER SMOKE OK");
    }

    private static void DefaultInstagramPresets()
    {
        var presets = AppSettings.CreateDefaultExportPresets();
        Assert(presets.TryGetValue("Instagram Square", out var square) && square.Width == 1080 && square.Height == 1080,
            "Instagram square preset missing");
        Assert(presets.TryGetValue("Instagram Portrait 4x5", out var portrait) && portrait.Width == 1080 && portrait.Height == 1350,
            "Instagram portrait preset missing");
        Assert(presets.TryGetValue("Instagram Landscape", out var landscape) && landscape.Width == 1080 && landscape.Height == 566,
            "Instagram landscape preset missing");
    }

    private static void SettingsMigrationAddsInstagramPresets()
    {
        var store = new MemorySettingsStore();
        store.Write("settings.json", "{\"SchemaVersion\":3,\"ExportPresets\":{}}");
        var settings = new SettingsService(store);
        Assert(settings.Settings.SchemaVersion == AppSettings.CurrentSchemaVersion &&
               settings.Settings.ExportPresets.ContainsKey("Instagram Square"),
            "schema migration did not seed Instagram presets");
    }

    private static void CredentialsStayOutOfSettings()
    {
        var vault = new TestCredentialStore();
        var service = new PublisherAccountService(vault);
        var account = new ConnectedPublisherAccount("tumblr",
            new OAuthToken("access-secret", "refresh-secret", DateTimeOffset.UtcNow.AddHours(1)),
            [new PublisherTarget("blog", "Blog")]);
        service.Save("tumblr", "client-secret", account);
        ConnectedPublisherAccount? loaded = service.Load("tumblr");
        Assert(loaded?.Token.AccessToken == "access-secret" && service.LoadClientSecret("tumblr") == "client-secret",
            "credential vault round trip failed");
        service.Save("tumblr", "", account);
        Assert(service.LoadClientSecret("tumblr") is null && service.Load("tumblr") is not null,
            "public OAuth client retained a stale client secret");
        service.Disconnect("tumblr");
        Assert(service.Load("tumblr") is null && service.LoadClientSecret("tumblr") is null,
            "credential vault disconnect failed");
    }

    private static async Task RefreshKeepsExistingTokenForPublicClient()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            "{\"access_token\":\"new-access\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"));
        var oauth = new PublisherOAuthService(new HttpClient(handler));
        var account = new ConnectedPublisherAccount("deviantart",
            new OAuthToken("old-access", "existing-refresh", DateTimeOffset.UtcNow.AddMinutes(-1)),
            [new PublisherTarget("me", "Artist")]);

        ConnectedPublisherAccount refreshed = await oauth.RefreshIfNeededAsync(account, "public-client", "");
        RecordedRequest request = handler.Requests.Single();
        Assert(refreshed.Token.AccessToken == "new-access" && refreshed.Token.RefreshToken == "existing-refresh",
            "OAuth refresh discarded an existing refresh token");
        Assert(request.Body.Contains("refresh_token=existing-refresh") &&
               !request.Body.Contains("client_secret", StringComparison.Ordinal),
            "public OAuth refresh request included incorrect credentials");
    }

    private static async Task TumblrRequest()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Created,
            "{\"response\":{\"id\":123,\"id_string\":\"123\"}}"));
        var publisher = new TumblrPublisher(new HttpClient(handler));
        ArtPublishResult result = await publisher.PublishAsync(new PublishDestination("token-t", "blog-uuid", "myblog"),
            Request(PublishState.Queue));
        RecordedRequest request = handler.Requests.Single();
        Assert(request.Uri.AbsoluteUri == "https://api.tumblr.com/v2/blog/blog-uuid/posts", "Tumblr endpoint mismatch");
        Assert(request.Authorization == "Bearer token-t", "Tumblr bearer token missing");
        Assert(request.Body.Contains("kawapaint-image") && request.Body.Contains("\"state\":\"queue\"") &&
               request.Body.Contains("alt text"), "Tumblr NPF multipart body mismatch");
        Assert(result.RemoteId == "123", "Tumblr response id mismatch");
    }

    private static async Task FacebookRequest()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            "{\"id\":\"photo-1\"}"));
        var publisher = new FacebookPublisher(new HttpClient(handler), "v24.0");
        ArtPublishResult result = await publisher.PublishAsync(new PublishDestination("page-token", "page-id", "Page"), Request());
        RecordedRequest request = handler.Requests.Single();
        Assert(request.Uri.AbsoluteUri == "https://graph.facebook.com/v24.0/page-id/photos", "Facebook endpoint mismatch");
        Assert(request.Authorization == "Bearer page-token", "Facebook bearer token missing");
        Assert(request.Body.Contains("alt_text_custom") && request.Body.Contains("#tag-one") &&
               request.Body.Contains("source"), "Facebook multipart fields missing");
        Assert(result.RemoteId == "photo-1", "Facebook response id mismatch");
    }

    private static async Task DeviantArtTwoStageRequest()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/stash/submit", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "{\"itemid\":42}")
            : Json(HttpStatusCode.OK, "{\"deviationid\":\"dev-1\",\"url\":\"https://www.deviantart.com/art/dev-1\"}"));
        var publisher = new DeviantArtPublisher(new HttpClient(handler));
        ArtPublishResult result = await publisher.PublishAsync(new PublishDestination("token-d", "me"),
            Request() with { IsMature = true, MatureLevel = "moderate", MatureClassifications = ["gore"], GalleryId = "gallery-1" });
        Assert(handler.Requests.Count == 2, "DeviantArt did not use upload then publish");
        Assert(handler.Requests[0].Uri.AbsolutePath.EndsWith("/stash/submit", StringComparison.Ordinal) &&
               handler.Requests[0].Body.Contains("artist_comments") && handler.Requests[0].Body.Contains("tags[]") &&
               handler.Requests[0].Body.Contains("tag_one") && handler.Requests[0].Body.Contains("is_ai_generated"),
            "DeviantArt upload body mismatch");
        Assert(handler.Requests[1].Uri.AbsolutePath.EndsWith("/stash/publish", StringComparison.Ordinal) &&
               handler.Requests[1].Body.Contains("itemid=42") && handler.Requests[1].Body.Contains("mature_level=moderate") &&
               handler.Requests[1].Body.Contains("galleryids%5B%5D=gallery-1") &&
               handler.Requests[1].UserAgent.Contains("KawaPaint", StringComparison.Ordinal) &&
               handler.Requests[1].MinorVersion == "20240701", "DeviantArt publish body/headers mismatch");
        Assert(result.RemoteId == "dev-1", "DeviantArt response id mismatch");
    }

    private static async Task DeviantArtRetainsStashOnFailure()
    {
        int call = 0;
        var handler = new RecordingHandler(_ => ++call == 1
            ? Json(HttpStatusCode.OK, "{\"itemid\":77}")
            : Json(HttpStatusCode.BadRequest, "{\"error\":\"bad metadata\"}"));
        try
        {
            await new DeviantArtPublisher(new HttpClient(handler)).PublishAsync(
                new PublishDestination("token", "me"), Request());
            throw new InvalidOperationException("expected DeviantArt publication failure");
        }
        catch (ArtPublishException ex)
        {
            Assert(ex.RetainedRemoteDraftId == "77" && ex.Message.Contains("Sta.sh", StringComparison.Ordinal),
                "DeviantArt failure lost retained Sta.sh id");
        }
    }

    private static async Task AmbiguousTransportFailureIsNotRetryable()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection reset"));
        try
        {
            await new TumblrPublisher(new HttpClient(handler)).PublishAsync(
                new PublishDestination("token", "blog"), Request());
            throw new InvalidOperationException("expected transport failure");
        }
        catch (ArtPublishException ex)
        {
            Assert(ex.OutcomeMayBeAmbiguous, "transport failure was not marked ambiguous");
        }
    }

    private static ArtPublishRequest Request(PublishState state = PublishState.Published) => new(
        [1, 2, 3, 4], "art.jpg", "image/jpeg", 10, 20, "Artwork", "Caption", "alt text",
        ["tag-one", "tag-two"], state);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed record RecordedRequest(Uri Uri, string? Authorization, string Body,
        string UserAgent, string? MinorVersion);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.RequestUri!, request.Headers.Authorization?.ToString(), body,
                request.Headers.UserAgent.ToString(), request.Headers.TryGetValues("dA-minor-version", out var values)
                    ? values.Single() : null));
            return response(request);
        }
    }

    private sealed class TestCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = new();
        public bool IsPersistent => true;
        public string? Read(string key) => _values.TryGetValue(key, out string? value) ? value : null;
        public void Write(string key, string secret) => _values[key] = secret;
        public void Delete(string key) => _values.Remove(key);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Publisher smoke test: " + message);
    }
}
