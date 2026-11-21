using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SecondBrain.Infrastructure.Vault;

namespace SecondBrain.Infrastructure.Ollama;

public sealed class RecordingHandler(HttpMessageHandler innerHandler, string cassetteDir)
    : DelegatingHandler(innerHandler ?? throw new ArgumentNullException(nameof(innerHandler)))
{
    private readonly string _cassetteDir = !string.IsNullOrWhiteSpace(cassetteDir)
        ? cassetteDir
        : throw new ArgumentException("Cassette directory cannot be null or whitespace.", nameof(cassetteDir));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (method, pathAndQuery, requestBody, key) = await HttpCassetteStore
            .ExtractRequestKeyAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var responseBody = string.Empty;
        if (response.Content is not null)
        {
            var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            responseBody = responseBytes.Length > 0 ? Encoding.UTF8.GetString(responseBytes) : string.Empty;
        }

        var entry = new HttpCassetteEntry(
            Method: method,
            Path: pathAndQuery,
            RequestBody: requestBody,
            StatusCode: (int)response.StatusCode,
            ResponseBody: responseBody);

        var cassettePath = HttpCassetteStore.GetCassettePath(_cassetteDir, key);
        var json = JsonSerializer.Serialize(entry, HttpCassetteStore.JsonOptions);
        await AtomicWriter.WriteAllTextAtomicAsync(cassettePath, json, cancellationToken).ConfigureAwait(false);

        return response;
    }
}

public sealed class ReplayHandler(string cassetteDir) : HttpMessageHandler
{
    private readonly string _cassetteDir = !string.IsNullOrWhiteSpace(cassetteDir)
        ? cassetteDir
        : throw new ArgumentException("Cassette directory cannot be null or whitespace.", nameof(cassetteDir));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var (method, pathAndQuery, _, key) = await HttpCassetteStore
            .ExtractRequestKeyAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var cassettePath = HttpCassetteStore.GetCassettePath(_cassetteDir, key);
        if (!File.Exists(cassettePath))
        {
            throw new FileNotFoundException(
                $"HTTP cassette not found for {method} {pathAndQuery} (key '{key}').",
                cassettePath);
        }

        var json = await File.ReadAllTextAsync(cassettePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var entry = JsonSerializer.Deserialize<HttpCassetteEntry>(json, HttpCassetteStore.JsonOptions)
            ?? throw new InvalidDataException($"Invalid HTTP cassette JSON in '{cassettePath}'.");

        return new HttpResponseMessage((HttpStatusCode)entry.StatusCode)
        {
            RequestMessage = request,
            Content = new StringContent(entry.ResponseBody ?? string.Empty, Encoding.UTF8, "application/json")
        };
    }
}

internal sealed record HttpCassetteEntry(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("request_body")] string RequestBody,
    [property: JsonPropertyName("status_code")] int StatusCode,
    [property: JsonPropertyName("response_body")] string ResponseBody);

internal static class HttpCassetteStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<(string Method, string PathAndQuery, string RequestBody, string Key)> ExtractRequestKeyAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var method = request.Method.Method.ToUpperInvariant();
        var pathAndQuery = GetPathAndQuery(request.RequestUri);

        var requestBody = string.Empty;
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            requestBody = bytes.Length > 0 ? Encoding.UTF8.GetString(bytes) : string.Empty;
        }

        var canonical = string.Concat(method, "\n", pathAndQuery, "\n", requestBody);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var key = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();

        return (method, pathAndQuery, requestBody, key);
    }

    public static string GetCassettePath(string cassetteDir, string key) =>
        Path.Combine(cassetteDir, $"{key}.json");

    private static string GetPathAndQuery(Uri? uri)
    {
        if (uri is null)
        {
            return "/";
        }

        if (uri.IsAbsoluteUri)
        {
            return uri.PathAndQuery;
        }

        var original = uri.OriginalString;
        return original.StartsWith('/') ? original : "/" + original;
    }
}