using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SecondBrain.Infrastructure.Ollama;
using Xunit;
using Xunit.Abstractions;

namespace SecondBrain.Tests.Live;

public sealed class OllamaLiveTests
{
    private static readonly Uri OllamaBaseUri = new("http://localhost:11434");
    private readonly ITestOutputHelper _output;

    public OllamaLiveTests(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    [Fact]
    public async Task BgeM3_LiveEmbedding_Returns1024DimsNormalizedAndDeterministic()
    {
        var options = new OllamaOptions(BaseUrl: OllamaBaseUri.ToString());
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        if (!await IsModelAvailableAsync(httpClient, options.EmbedModel, CancellationToken.None))
        {
            return;
        }

        var client = new OllamaEmbeddingClient(httpClient, options);
        const string text = "Встреча по архитектуре SecondBrain и семантическому поиску";

        var first = await client.EmbedQueryAsync(text);
        var second = await client.EmbedQueryAsync(text);

        Assert.Equal(1024, first.Length);
        Assert.Equal(1024, second.Length);

        var norm1 = ComputeL2Norm(first);
        var norm2 = ComputeL2Norm(second);
        Assert.InRange(norm1, 0.999f, 1.001f);
        Assert.InRange(norm2, 0.999f, 1.001f);

        var cosine = ComputeCosineSimilarity(first, second);
        _output.WriteLine($"bge-m3 norm1={norm1:F6}, norm2={norm2:F6}, cosine={cosine:F6}");
        Assert.True(cosine >= 0.999f, $"Expected deterministic cosine >= 0.999, got {cosine}.");
    }

    [Fact]
    public async Task TLite_LiveExtract_ReturnsDeserializableStructuredObject()
    {
        var options = new OllamaOptions(BaseUrl: OllamaBaseUri.ToString());
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        if (!await IsModelAvailableAsync(httpClient, options.ChatModel, CancellationToken.None))
        {
            return;
        }

        var router = new OllamaChatRouter(httpClient, options);
        var chat = router.Resolve("extract");

        const string schema = """
            {
              "type": "object",
              "properties": {
                "title": { "type": "string" },
                "topic": { "type": "string" },
                "tags": {
                  "type": "array",
                  "items": { "type": "string" }
                }
              },
              "required": ["title", "topic", "tags"],
              "additionalProperties": false
            }
            """;

        var extracted = await chat.ExtractAsync<LiveNoteExtraction>(
            systemPrompt: "Extract structured metadata from the user note strictly according to the JSON schema.",
            userPrompt: "Заметка: настройка локального поиска на sqlite и bge-m3 для проекта SecondBrain.",
            jsonSchema: schema);

        Assert.NotNull(extracted);
        Assert.False(string.IsNullOrWhiteSpace(extracted!.Title));
        Assert.False(string.IsNullOrWhiteSpace(extracted.Topic));
        Assert.NotNull(extracted.Tags);
        _output.WriteLine($"T-lite extracted: Title='{extracted.Title}', Topic='{extracted.Topic}', Tags=[{string.Join(", ", extracted.Tags)}]");
    }

    private async Task<bool> IsModelAvailableAsync(
        HttpClient httpClient,
        string expectedModel,
        CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(2));

        OllamaTagsResponse? tags;
        try
        {
            using var response = await httpClient.GetAsync(
                new Uri(OllamaBaseUri, "/api/tags"),
                probeCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _output.WriteLine($"Skipping live Ollama test: GET /api/tags returned HTTP {(int)response.StatusCode}.");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(probeCts.Token);
            tags = JsonSerializer.Deserialize<OllamaTagsResponse>(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            _output.WriteLine($"Skipping live Ollama test: Ollama is unavailable at {OllamaBaseUri} ({ex.Message}).");
            return false;
        }

        if (tags?.Models is not null)
        {
            foreach (var model in tags.Models)
            {
                if (MatchesModel(model.Name, expectedModel) || MatchesModel(model.Model, expectedModel))
                {
                    return true;
                }
            }
        }

        _output.WriteLine($"Skipping live Ollama test: model '{expectedModel}' is not installed in Ollama.");
        return false;
    }

    private static bool MatchesModel(string? installedName, string expectedModel)
    {
        if (string.IsNullOrWhiteSpace(installedName))
        {
            return false;
        }

        return string.Equals(installedName, expectedModel, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(installedName, expectedModel + ":latest", StringComparison.OrdinalIgnoreCase);
    }

    private static float ComputeL2Norm(float[] vector)
    {
        var sum = 0.0;
        for (var i = 0; i < vector.Length; i++)
        {
            sum += (double)vector[i] * vector[i];
        }

        return (float)Math.Sqrt(sum);
    }

    private static float ComputeCosineSimilarity(float[] a, float[] b)
    {
        var dot = 0.0;
        var normA = 0.0;
        var normB = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0.0 ? 0.0f : (float)(dot / denom);
    }

    private sealed record LiveNoteExtraction(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("topic")] string Topic,
        [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags);

    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] IReadOnlyList<OllamaTagModel>? Models);

    private sealed record OllamaTagModel(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("model")] string? Model);
}