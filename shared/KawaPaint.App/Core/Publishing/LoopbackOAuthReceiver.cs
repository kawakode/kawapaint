using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KawaPaint.App.Core.Publishing;

internal static class LoopbackOAuthReceiver
{
    public const int Port = 43817;
    public static readonly Uri RedirectUri = new($"http://127.0.0.1:{Port}/callback/");

    public static async Task<string> AuthorizeAsync(Func<string, Uri> buildAuthorizationUri,
        CancellationToken cancellationToken)
    {
        string state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var listener = new TcpListener(IPAddress.Loopback, Port);
        try { listener.Start(1); }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"OAuth callback port {Port} is already in use.", ex);
        }

        try
        {
            Uri authorizationUri = buildAuthorizationUri(state);
            Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri) { UseShellExecute = true });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            string? firstLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false))) { }

            if (firstLine is null || !firstLine.StartsWith("GET ", StringComparison.Ordinal))
                throw new InvalidOperationException("OAuth callback was not an HTTP GET request.");
            int end = firstLine.IndexOf(' ', 4);
            if (end < 0) throw new InvalidOperationException("OAuth callback request was malformed.");
            var callback = new Uri(RedirectUri, firstLine[4..end]);
            var query = ParseQuery(callback.Query);
            bool stateOk = query.TryGetValue("state", out string? returnedState) && returnedState.Length == state.Length &&
                           System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                               Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(returnedState));
            if (!stateOk) throw new InvalidOperationException("OAuth callback state did not match.");

            string responseText;
            if (query.TryGetValue("code", out string? code) && !string.IsNullOrWhiteSpace(code))
            {
                responseText = "KawaPaint is connected. You can close this browser tab.";
                await WriteResponseAsync(stream, responseText, timeout.Token).ConfigureAwait(false);
                return code;
            }

            string error = query.TryGetValue("error_description", out string? description) ? description :
                query.TryGetValue("error", out string? errorCode) ? errorCode : "Authorization was cancelled.";
            responseText = "KawaPaint was not connected: " + error;
            await WriteResponseAsync(stream, responseText, timeout.Token).ConfigureAwait(false);
            throw new InvalidOperationException(error);
        }
        finally { listener.Stop(); }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            string value = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    private static async Task WriteResponseAsync(Stream stream, string message, CancellationToken cancellationToken)
    {
        string escaped = WebUtility.HtmlEncode(message);
        byte[] body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>KawaPaint</title><p>{escaped}</p>");
        byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }
}
