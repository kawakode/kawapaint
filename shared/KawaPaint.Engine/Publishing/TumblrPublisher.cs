using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KawaPaint.Engine.Publishing;

public sealed class TumblrPublisher : IArtPublisher
{
    private readonly HttpClient _http;

    public TumblrPublisher(HttpClient? httpClient = null) => _http = httpClient ?? new HttpClient();

    public string Id => "tumblr";
    public string DisplayName => "Tumblr";
    public IReadOnlySet<PublishState> SupportedStates { get; } =
        new HashSet<PublishState> { PublishState.Published, PublishState.Draft, PublishState.Queue };

    public void Validate(PublishDestination destination, ArtPublishRequest request)
    {
        if (string.IsNullOrWhiteSpace(destination.AccessToken)) throw new ArgumentException("Tumblr is not connected.");
        if (string.IsNullOrWhiteSpace(destination.TargetId)) throw new ArgumentException("Choose a Tumblr blog.");
        if (request.ImageBytes.Length == 0) throw new ArgumentException("The exported image is empty.");
        if (!SupportedStates.Contains(request.State)) throw new ArgumentException("Tumblr does not support that post state.");
    }

    public async Task<ArtPublishResult> PublishAsync(PublishDestination destination,
        ArtPublishRequest request, CancellationToken cancellationToken = default)
    {
        Validate(destination, request);
        const string mediaId = "kawapaint-image";
        var blocks = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Title))
            blocks.Add(new { type = "text", subtype = "heading1", text = request.Title.Trim() });
        blocks.Add(new
        {
            type = "image",
            media = new[] { new { type = request.MimeType, identifier = mediaId, width = request.Width, height = request.Height } },
            alt_text = EmptyToNull(request.AltText)
        });
        if (!string.IsNullOrWhiteSpace(request.Caption))
            blocks.Add(new { type = "text", subtype = (string?)null, text = request.Caption.Trim() });

        string state = request.State switch
        {
            PublishState.Draft => "draft",
            PublishState.Queue => "queue",
            _ => "published"
        };
        string json = JsonSerializer.Serialize(new
        {
            content = blocks,
            state,
            tags = string.Join(',', request.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()))
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        using var multipart = new MultipartFormDataContent();
        var jsonContent = new StringContent(json, Encoding.UTF8, "application/json");
        multipart.Add(jsonContent, "json");
        var image = new ByteArrayContent(request.ImageBytes);
        image.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MimeType);
        multipart.Add(image, mediaId, request.FileName);

        string blog = Uri.EscapeDataString(destination.TargetId.Trim());
        using var message = new HttpRequestMessage(HttpMethod.Post, $"https://api.tumblr.com/v2/blog/{blog}/posts")
        { Content = multipart };
        message.Headers.UserAgent.ParseAdd("KawaPaint/1.0");
        PublisherHttp.Bearer(message, destination.AccessToken);
        using JsonDocument response = await PublisherHttp.SendJsonAsync(_http, message,
            "Tumblr publishing", cancellationToken).ConfigureAwait(false);
        JsonElement root = response.RootElement.TryGetProperty("response", out var wrapped)
            ? wrapped : response.RootElement;
        string id = root.TryGetProperty("id_string", out var idString)
            ? idString.GetString() ?? string.Empty
            : PublisherHttp.RequiredString(root, "id", "Tumblr publishing");
        string blogName = destination.TargetName ?? destination.TargetId;
        Uri? url = Uri.TryCreate($"https://www.tumblr.com/{Uri.EscapeDataString(blogName)}/{id}", UriKind.Absolute, out var parsed)
            ? parsed : null;
        return new ArtPublishResult(id, url, request.State == PublishState.Published
            ? "Published to Tumblr" : $"Saved to Tumblr as {state}");
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
