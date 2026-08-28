using System.Net;

namespace KawaPaint.Engine.Publishing;

public enum PublishState
{
    Published,
    Draft,
    Queue
}

public sealed record PublishDestination(string AccessToken, string TargetId, string? TargetName = null);

public sealed record ArtPublishRequest(
    byte[] ImageBytes,
    string FileName,
    string MimeType,
    int Width,
    int Height,
    string Title,
    string Caption,
    string AltText,
    IReadOnlyList<string> Tags,
    PublishState State = PublishState.Published,
    bool IsMature = false,
    string? MatureLevel = null,
    IReadOnlyList<string>? MatureClassifications = null,
    string? GalleryId = null,
    bool IsAiGenerated = false,
    bool NoAi = true);

public sealed record ArtPublishResult(string RemoteId, Uri? Url, string Message);

public sealed class ArtPublishException : Exception
{
    public ArtPublishException(string message, HttpStatusCode? statusCode = null,
        bool outcomeMayBeAmbiguous = false, string? retainedRemoteDraftId = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        OutcomeMayBeAmbiguous = outcomeMayBeAmbiguous;
        RetainedRemoteDraftId = retainedRemoteDraftId;
    }

    public HttpStatusCode? StatusCode { get; }
    public bool OutcomeMayBeAmbiguous { get; }
    public string? RetainedRemoteDraftId { get; }
}

public interface IArtPublisher
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlySet<PublishState> SupportedStates { get; }
    void Validate(PublishDestination destination, ArtPublishRequest request);
    Task<ArtPublishResult> PublishAsync(PublishDestination destination,
        ArtPublishRequest request, CancellationToken cancellationToken = default);
}
