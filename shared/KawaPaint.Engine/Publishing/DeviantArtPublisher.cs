using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KawaPaint.Engine.Publishing;

public sealed class DeviantArtPublisher : IArtPublisher
{
    private readonly HttpClient _http;

    public DeviantArtPublisher(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new HttpClientHandler
        { AutomaticDecompression = System.Net.DecompressionMethods.All });
    }

    public string Id => "deviantart";
    public string DisplayName => "DeviantArt";
    public IReadOnlySet<PublishState> SupportedStates { get; } =
        new HashSet<PublishState> { PublishState.Published };

    public void Validate(PublishDestination destination, ArtPublishRequest request)
    {
        if (string.IsNullOrWhiteSpace(destination.AccessToken)) throw new ArgumentException("DeviantArt is not connected.");
        if (string.IsNullOrWhiteSpace(request.Title)) throw new ArgumentException("DeviantArt requires a title.");
        if (request.State != PublishState.Published) throw new ArgumentException("DeviantArt publishing currently posts immediately.");
        if (request.ImageBytes.Length == 0) throw new ArgumentException("The exported image is empty.");
        if (request.IsMature && string.IsNullOrWhiteSpace(request.MatureLevel))
            throw new ArgumentException("Choose a mature-content level for DeviantArt.");
        if (request.IsMature && request.MatureClassifications?.Any(value => !MatureClassifications.Contains(value)) == true)
            throw new ArgumentException("A DeviantArt mature-content classification is invalid.");
    }

    public async Task<ArtPublishResult> PublishAsync(PublishDestination destination,
        ArtPublishRequest request, CancellationToken cancellationToken = default)
    {
        Validate(destination, request);
        string itemId = await UploadToStashAsync(destination, request, cancellationToken).ConfigureAwait(false);
        try
        {
            return await PublishStashItemAsync(destination, request, itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (ArtPublishException ex)
        {
            throw new ArtPublishException(ex.Message + $" The upload remains in Sta.sh as item {itemId}.",
                ex.StatusCode, ex.OutcomeMayBeAmbiguous, itemId, ex);
        }
    }

    private async Task<string> UploadToStashAsync(PublishDestination destination,
        ArtPublishRequest request, CancellationToken cancellationToken)
    {
        using var multipart = new MultipartFormDataContent();
        var image = new ByteArrayContent(request.ImageBytes);
        image.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MimeType);
        multipart.Add(image, "file", request.FileName);
        multipart.Add(new StringContent(request.Title.Trim()), "title");
        if (!string.IsNullOrWhiteSpace(request.Caption))
            multipart.Add(new StringContent(request.Caption.Trim()), "artist_comments");
        foreach (string tag in CleanTags(request.Tags)) multipart.Add(new StringContent(tag), "tags[]");
        multipart.Add(new StringContent(request.IsAiGenerated ? "true" : "false"), "is_ai_generated");
        multipart.Add(new StringContent(request.NoAi ? "true" : "false"), "noai");

        using var message = new HttpRequestMessage(HttpMethod.Post,
            "https://www.deviantart.com/api/v1/oauth2/stash/submit") { Content = multipart };
        AddRequiredHeaders(message);
        PublisherHttp.Bearer(message, destination.AccessToken);
        using JsonDocument response = await PublisherHttp.SendJsonAsync(_http, message,
            "DeviantArt Sta.sh upload", cancellationToken).ConfigureAwait(false);
        JsonElement root = response.RootElement;
        if (root.TryGetProperty("itemid", out var item))
            return item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText();
        return PublisherHttp.RequiredString(root, "stashid", "DeviantArt Sta.sh upload");
    }

    private async Task<ArtPublishResult> PublishStashItemAsync(PublishDestination destination,
        ArtPublishRequest request, string itemId, CancellationToken cancellationToken)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("itemid", itemId),
            new("title", request.Title.Trim()),
            new("artist_comments", request.Caption.Trim()),
            new("is_mature", request.IsMature ? "true" : "false"),
            new("allow_comments", "true"),
            new("is_ai_generated", request.IsAiGenerated ? "true" : "false"),
            new("noai", request.NoAi ? "true" : "false")
        };
        foreach (string tag in CleanTags(request.Tags)) fields.Add(new("tags[]", tag));
        if (!string.IsNullOrWhiteSpace(request.GalleryId)) fields.Add(new("galleryids[]", request.GalleryId.Trim()));
        if (request.IsMature)
        {
            fields.Add(new("mature_level", request.MatureLevel!));
            foreach (string classification in request.MatureClassifications ?? Array.Empty<string>())
                fields.Add(new("mature_classification[]", classification));
        }

        using var message = new HttpRequestMessage(HttpMethod.Post,
            "https://www.deviantart.com/api/v1/oauth2/stash/publish")
        { Content = new FormUrlEncodedContent(fields) };
        AddRequiredHeaders(message);
        PublisherHttp.Bearer(message, destination.AccessToken);
        using JsonDocument response = await PublisherHttp.SendJsonAsync(_http, message,
            "DeviantArt publication", cancellationToken).ConfigureAwait(false);
        string id = PublisherHttp.RequiredString(response.RootElement, "deviationid", "DeviantArt publication");
        Uri? url = null;
        if (response.RootElement.TryGetProperty("url", out var urlNode))
            Uri.TryCreate(urlNode.GetString(), UriKind.Absolute, out url);
        return new ArtPublishResult(id, url, "Published to DeviantArt");
    }

    private static readonly IReadOnlySet<string> MatureClassifications = new HashSet<string>(
        ["nudity", "sexual", "gore", "language", "ideology"], StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> CleanTags(IReadOnlyList<string> tags) => tags
        .Select(NormalizeTag).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeTag(string value)
    {
        var normalized = new StringBuilder(value.Length);
        foreach (char c in value.Trim().TrimStart('#'))
        {
            if (char.IsLetterOrDigit(c) || c == '_') normalized.Append(c);
            else if ((char.IsWhiteSpace(c) || c == '-') && normalized.Length > 0 && normalized[^1] != '_')
                normalized.Append('_');
        }
        return normalized.ToString().Trim('_');
    }

    private static void AddRequiredHeaders(HttpRequestMessage message)
    {
        message.Headers.UserAgent.ParseAdd("KawaPaint/1.0");
        message.Headers.AcceptEncoding.ParseAdd("gzip");
        message.Headers.TryAddWithoutValidation("dA-minor-version", "20240701");
    }
}
