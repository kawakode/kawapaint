using System.Net.Http.Headers;
using System.Text.Json;

namespace KawaPaint.Engine.Publishing;

internal static class PublisherHttp
{
    public static void Bearer(HttpRequestMessage message, string accessToken) =>
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    public static async Task<JsonDocument> SendJsonAsync(HttpClient http, HttpRequestMessage request,
        string operation, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new ArtPublishException($"{operation} did not return a response. Check the platform before retrying; the post may already exist.",
                outcomeMayBeAmbiguous: true, innerException: ex);
        }

        using (response)
        {
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = ErrorDetail(body);
                throw new ArtPublishException($"{operation} failed ({(int)response.StatusCode} {response.ReasonPhrase})" +
                    (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail), response.StatusCode);
            }

            try { return JsonDocument.Parse(body); }
            catch (JsonException ex)
            {
                throw new ArtPublishException($"{operation} returned an invalid response.",
                    response.StatusCode, innerException: ex);
            }
        }
    }

    public static string RequiredString(JsonElement element, string property, string operation)
    {
        if (element.TryGetProperty(property, out var value))
        {
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!;
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        throw new ArtPublishException($"{operation} response did not include {property}.");
    }

    private static string ErrorDetail(byte[] body)
    {
        if (body.Length == 0) return string.Empty;
        try
        {
            using var json = JsonDocument.Parse(body);
            JsonElement root = json.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return Limit(error.GetString());
                if (error.TryGetProperty("message", out var message)) return Limit(message.GetString());
                return Limit(error.GetRawText());
            }
            if (root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("msg", out var msg))
                return Limit(msg.GetString());
            if (root.TryGetProperty("error_description", out var description))
                return Limit(description.GetString());
            return Limit(root.GetRawText());
        }
        catch { return Limit(System.Text.Encoding.UTF8.GetString(body)); }
    }

    private static string Limit(string? value)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= 600 ? value : value[..600] + "…";
    }
}
