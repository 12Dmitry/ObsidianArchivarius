using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SecondBrain.Core.Ports;

namespace SecondBrain.Infrastructure.Ollama;

public sealed class OllamaEmbeddingClient : IEmbeddingClient
{
    private const int ExpectedDimensions = 1024;
    private const string KeepAliveValue = "10m";

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly Uri _embedUri;

    public OllamaEmbeddingClient(HttpClient httpClient, OllamaOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new OllamaOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.EmbedModel);
        if (_options.EmbedBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.EmbedBatchSize,
                "EmbedBatchSize must be a positive integer.");
        }

        var baseUri = OllamaEndpointHelper.ResolveBaseUri(_httpClient, options);
        _embedUri = OllamaEndpointHelper.BuildEndpointUri(baseUri, "/api/embed");
    }

    public string ModelId => _options.EmbedModel;
    public int Dimensions => ExpectedDimensions;
    public string QueryPrefix => string.Empty;

    public async Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ct.ThrowIfCancellationRequested();

        if (texts.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        for (var i = 0; i < texts.Count; i++)
        {
            if (texts[i] is null)
            {
                throw new ArgumentException($"Input text at index {i} must not be null.", nameof(texts));
            }
        }

        var results = new List<float[]>(texts.Count);

        for (var offset = 0; offset < texts.Count; offset += _options.EmbedBatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var count = Math.Min(_options.EmbedBatchSize, texts.Count - offset);
            var batch = new string[count];
            for (var i = 0; i < count; i++)
            {
                batch[i] = texts[offset + i];
            }

            var batchEmbeddings = await EmbedBatchAsync(batch, ct).ConfigureAwait(false);
            results.AddRange(batchEmbeddings);
        }

        return results;
    }

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var embeddings = await EmbedDocumentsAsync([text], ct).ConfigureAwait(false);
        return embeddings[0];
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(string[] batch, CancellationToken ct)
    {
        var payload = new OllamaEmbedRequest(
            Model: _options.EmbedModel,
            Input: batch,
            Truncate: false,
            KeepAlive: KeepAliveValue,
            Options: new OllamaEmbedRequestOptions(NumGpu: 0));

        var requestJson = JsonSerializer.Serialize(payload, RequestJsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, _embedUri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.BadRequest ||
            (!response.IsSuccessStatusCode && IsTruncationError(responseBody)))
        {
            throw new InvalidOperationException(
                $"Ollama /api/embed rejected input due to context length / truncation (HTTP {(int)response.StatusCode}, truncate=false): {responseBody}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Ollama /api/embed failed with status {(int)response.StatusCode} ({response.ReasonPhrase}): {responseBody}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var parsed = JsonSerializer.Deserialize<OllamaEmbedResponse>(responseBody, ResponseJsonOptions)
            ?? throw new InvalidOperationException("Ollama /api/embed returned an empty JSON response.");

        if (parsed.Embeddings is null)
        {
            throw new InvalidOperationException("Ollama /api/embed response did not contain 'embeddings'.");
        }

        if (parsed.Embeddings.Count != batch.Length)
        {
            throw new InvalidOperationException(
                $"Ollama /api/embed returned {parsed.Embeddings.Count} vectors for batch size {batch.Length}.");
        }

        var normalizedBatch = new float[batch.Length][];
        for (var i = 0; i < parsed.Embeddings.Count; i++)
        {
            var vector = parsed.Embeddings[i];
            if (vector is null || vector.Length != ExpectedDimensions)
            {
                throw new InvalidOperationException(
                    $"Expected embedding vector length {ExpectedDimensions} for model '{_options.EmbedModel}', but got {vector?.Length ?? 0}.");
            }

            normalizedBatch[i] = NormalizeL2(vector);
        }

        return normalizedBatch;
    }

    private static float[] NormalizeL2(float[] vector)
    {
        var sumSquares = 0.0;
        for (var i = 0; i < vector.Length; i++)
        {
            var value = vector[i];
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new InvalidOperationException("Embedding vector contains non-finite values and cannot be L2-normalized.");
            }

            sumSquares += (double)value * value;
        }

        var norm = (float)Math.Sqrt(sumSquares);
        if (sumSquares == 0.0 || norm == 0.0f || float.IsNaN(norm) || float.IsInfinity(norm))
        {
            throw new InvalidOperationException("Embedding vector has zero or non-finite L2 norm and cannot be normalized.");
        }

        var normDouble = Math.Sqrt(sumSquares);
        var normalized = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            normalized[i] = (float)(vector[i] / normDouble);
        }

        return normalized;
    }

    private static bool IsTruncationError(string responseBody) =>
        responseBody.Contains("truncat", StringComparison.OrdinalIgnoreCase) ||
        responseBody.Contains("context length", StringComparison.OrdinalIgnoreCase) ||
        responseBody.Contains("input length", StringComparison.OrdinalIgnoreCase);

    private sealed record OllamaEmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("truncate")] bool Truncate,
        [property: JsonPropertyName("keep_alive")] string KeepAlive,
        [property: JsonPropertyName("options")] OllamaEmbedRequestOptions Options);

    private sealed record OllamaEmbedRequestOptions(
        [property: JsonPropertyName("num_gpu")] int NumGpu);

    private sealed record OllamaEmbedResponse(
        [property: JsonPropertyName("embeddings")] IReadOnlyList<float[]?>? Embeddings);
}