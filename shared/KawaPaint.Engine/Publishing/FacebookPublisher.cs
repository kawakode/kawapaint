using System.Net.Http.Headers;
using System.Text.Json;

namespace KawaPaint.Engine.Publishing;

public sealed class FacebookPublisher : IArtPublisher
{
    public const string DefaultGraphVersion = "v24.0";
    private readonly HttpClient _http;
    private readonly string _graphBase;

    public FacebookPublisher(HttpClient? httpClient = null, string graphVersion = DefaultGraphVersion)
    {
        _http = httpClient ?? new HttpClient();
        _graphBase = "https://graph.facebook.com/" + graphVersion.Trim('/');
    }

    public string Id => "facebook";
    public string DisplayName => "Facebook Page";
    public IReadOnlySet<PublishState> SupportedStates { get; } =
        new HashSet<PublishState> { PublishState.Published };

    public void Validate(PublishDestination destination, ArtPublishRequest request)
    {
        if (string.IsNullOrWhiteSpace(destination.AccessToken)) throw new ArgumentException("Facebook is not connected.");
        if (string.IsNullOrWhiteSpace(destination.TargetId)) throw new ArgumentException("Choose a Facebook Page.");
        if (request.State != PublishState.Published) throw new ArgumentException("Facebook Page publishing currently posts immediately.");
        if (request.ImageBytes.Length == 0) throw new ArgumentException("The exported image is empty.");
    }

    public async Task<ArtPublishResult> PublishAsync(PublishDestination destination,
        ArtPublishRequest request, CancellationToken cancellationToken = default)
    {
        Validate(destination, request);
        using var multipart = new MultipartFormDataContent();
        var image = new ByteArrayContent(request.ImageBytes);
        image.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MimeType);
        multipart.Add(image, "source", request.FileName);
        string messageText = JoinCaption(request.Title, request.Caption, request.Tags);
        if (messageText.Length > 0) multipart.Add(new StringContent(messageText), "message");
        if (!string.IsNullOrWhiteSpace(request.AltText))
            multipart.Add(new StringContent(request.AltText.Trim()), "alt_text_custom");
        multipart.Add(new StringContent("true"), "published");

        string page = Uri.EscapeDataString(destination.TargetId.Trim());
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{_graphBase}/{page}/photos")
        { Content = multipart };
        PublisherHttp.Bearer(message, destination.AccessToken);
        using JsonDocument response = await PublisherHttp.SendJsonAsync(_http, message,
            "Facebook Page publishing", cancellationToken).ConfigureAwait(false);
        string photoId = PublisherHttp.RequiredString(response.RootElement, "id", "Facebook Page publishing");
        string? postId = response.RootElement.TryGetProperty("post_id", out var post) ? post.GetString() : null;
        Uri? url = !string.IsNullOrWhiteSpace(postId) &&
                   Uri.TryCreate("https://www.facebook.com/" + postId, UriKind.Absolute, out var parsed)
            ? parsed : null;
        return new ArtPublishResult(photoId, url, "Published to Facebook Page");
    }

    private static string JoinCaption(string title, string caption, IReadOnlyList<string> tags)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) parts.Add(title.Trim());
        if (!string.IsNullOrWhiteSpace(caption)) parts.Add(caption.Trim());
        string tagLine = string.Join(' ', tags.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).Select(t => "#" + t));
        if (tagLine.Length > 0) parts.Add(tagLine);
        return string.Join("\n\n", parts);
    }
}
