using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SecondBrain.Core.Ports;

namespace SecondBrain.Infrastructure.Ollama;

public sealed record OllamaOptions(
    string BaseUrl = "http://localhost:11434",
    string ChatModel = "hf.co/t-tech/T-lite-it-2.1-GGUF:Q4_K_M",
    string EmbedModel = "bge-m3",
    int EmbedBatchSize = 16);

public sealed class OllamaChatRouter : IChatRouter
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly Uri _baseUri;

    public OllamaChatRouter(HttpClient httpClient, OllamaOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new OllamaOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ChatModel);
        _baseUri = OllamaEndpointHelper.ResolveBaseUri(_httpClient, options);
    }

    public IChatClient Resolve(string profile)
    {
        var llmProfile = Describe(profile);
        return new OllamaChatClient(_httpClient, llmProfile, _baseUri);
    }

    public LlmProfile Describe(string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        return profile.Trim().ToLowerInvariant() switch
        {
            "extract" => new LlmProfile(
                Provider: "ollama",
                Model: _options.ChatModel,
                NumCtx: 8192,
                Temperature: 0.15,
                TopP: 0.8,
                TopK: 20,
                PresencePenalty: 0.0,
                MaxOutputTokens: 2048,
                TimeoutSeconds: 120),
            "prose" => new LlmProfile(
                Provider: "ollama",
                Model: _options.ChatModel,
                NumCtx: 8192,
                Temperature: 0.7,
                TopP: 0.8,
                TopK: 20,
                PresencePenalty: 1.0,
                MaxOutputTokens: 2048,
                TimeoutSeconds: 120),
            _ => throw new ArgumentException(
                $"Unknown LLM profile '{profile}'. Supported profiles: extract, prose.",
                nameof(profile))
        };
    }
}

public sealed class OllamaChatClient : IChatClient
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _httpClient;
    private readonly LlmProfile _profile;
    private readonly Uri _chatUri;

    public OllamaChatClient(HttpClient httpClient, LlmProfile profile, OllamaOptions? options = null)
        : this(
            httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
            profile ?? throw new ArgumentNullException(nameof(profile)),
            OllamaEndpointHelper.ResolveBaseUri(
                httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
                options))
    {
    }

    internal OllamaChatClient(HttpClient httpClient, LlmProfile profile, Uri baseUri)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(_profile.Model);

        _chatUri = OllamaEndpointHelper.BuildEndpointUri(baseUri, "/api/chat");
    }

    public LlmProfile Profile => _profile;

    public async Task<T?> ExtractAsync<T>(
        string systemPrompt,
        string userPrompt,
        string jsonSchema,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(userPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonSchema);

        JsonElement formatElement;
        using (var schemaDoc = JsonDocument.Parse(jsonSchema))
        {
            if (schemaDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("JSON schema must be a JSON object.", nameof(jsonSchema));
            }

            formatElement = schemaDoc.RootElement.Clone();
        }

        var content = await SendChatRequestAsync(systemPrompt, userPrompt, formatElement, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(content))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(content, ResponseJsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(userPrompt);

        return SendChatRequestAsync(systemPrompt, userPrompt, format: null, ct);
    }

    private async Task<string> SendChatRequestAsync(
        string systemPrompt,
        string userPrompt,
        JsonElement? format,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var requestPayload = new OllamaChatRequest(
            Model: _profile.Model,
            Messages:
            [
                new OllamaChatMessage("system", systemPrompt),
                new OllamaChatMessage("user", userPrompt)
            ],
            Stream: false,
            Format: format,
            Options: new OllamaChatRequestOptions(
                Temperature: _profile.Temperature,
                TopP: _profile.TopP,
                TopK: _profile.TopK,
                PresencePenalty: _profile.PresencePenalty,
                NumCtx: _profile.NumCtx,
                NumPredict: _profile.MaxOutputTokens));

        var requestJson = JsonSerializer.Serialize(requestPayload, RequestJsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, _chatUri)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        using var timeoutCts = _profile.TimeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts is not null)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_profile.TimeoutSeconds));
        }

        var effectiveCt = timeoutCts?.Token ?? ct;

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, effectiveCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutCts is { IsCancellationRequested: true })
        {
            throw new TimeoutException(
                $"Ollama /api/chat request timed out after {_profile.TimeoutSeconds} seconds.",
                ex);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(effectiveCt).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Ollama /api/chat failed with status {(int)response.StatusCode} ({response.ReasonPhrase}): {responseBody}",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            var chatResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseBody, ResponseJsonOptions);
            if (chatResponse?.Message?.Content is null)
            {
                throw new InvalidOperationException("Ollama /api/chat response did not contain message.content.");
            }

            return chatResponse.Message.Content;
        }
    }

    private sealed record OllamaChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<OllamaChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Format,
        [property: JsonPropertyName("options")] OllamaChatRequestOptions Options);

    private sealed record OllamaChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record OllamaChatRequestOptions(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("top_p")] double TopP,
        [property: JsonPropertyName("top_k")] int TopK,
        [property: JsonPropertyName("presence_penalty")] double PresencePenalty,
        [property: JsonPropertyName("num_ctx")] int NumCtx,
        [property: JsonPropertyName("num_predict")] int NumPredict);

    private sealed record OllamaChatResponse(
        [property: JsonPropertyName("message")] OllamaChatResponseMessage? Message);

    private sealed record OllamaChatResponseMessage(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content);
}

internal static class OllamaEndpointHelper
{
    public static Uri ResolveBaseUri(HttpClient httpClient, OllamaOptions? options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        string rawBaseUrl;
        if (options is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);
            rawBaseUrl = options.BaseUrl;
        }
        else if (httpClient.BaseAddress is not null)
        {
            rawBaseUrl = httpClient.BaseAddress.ToString();
        }
        else
        {
            rawBaseUrl = new OllamaOptions().BaseUrl;
        }

        var normalized = rawBaseUrl.Trim().TrimEnd('/') + "/";
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException($"Invalid Ollama base URL '{rawBaseUrl}'.", nameof(options));
        }

        return baseUri;
    }

    public static Uri BuildEndpointUri(Uri baseUri, string relativeEndpoint)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeEndpoint);

        return new Uri(baseUri, relativeEndpoint.TrimStart('/'));
    }
}