using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SecondBrain.Infrastructure.Ollama;
using Xunit;

namespace SecondBrain.Tests.Ollama;

public sealed class OllamaClientTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExtractAsync_WithExtractProfile_SendsExpectedChatPayloadAndDeserializesCaseInsensitive()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var innerJson = """{"TITLE":"SecondBrain Note","TAGS":["ollama","dotnet"]}""";
            var envelope = JsonSerializer.Serialize(new
            {
                message = new { role = "assistant", content = innerJson }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var options = new OllamaOptions(BaseUrl: "http://localhost:11434", ChatModel: "hf.co/t-tech/T-lite-it-2.1-GGUF:Q4_K_M");
        var router = new OllamaChatRouter(httpClient, options);
        var chat = router.Resolve("extract");

        const string schema = """
            {
              "type": "object",
              "properties": {
                "title": { "type": "string" },
                "tags": { "type": "array", "items": { "type": "string" } }
              },
              "required": ["title", "tags"],
              "additionalProperties": false
            }
            """;

        var result = await chat.ExtractAsync<ExtractedNote>("system prompt", "user prompt", schema);

        result.Should().NotBeNull();
        result!.Title.Should().Be("SecondBrain Note");
        result.Tags.Should().Equal("ollama", "dotnet");

        handler.Requests.Should().ContainSingle();
        var recorded = handler.Requests[0];
        recorded.Method.Should().Be(HttpMethod.Post);
        recorded.PathAndQuery.Should().Be("/api/chat");

        using var bodyDoc = JsonDocument.Parse(recorded.Body);
        var root = bodyDoc.RootElement;
        root.GetProperty("model").GetString().Should().Be("hf.co/t-tech/T-lite-it-2.1-GGUF:Q4_K_M");
        root.GetProperty("stream").GetBoolean().Should().BeFalse();

        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(2);
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[0].GetProperty("content").GetString().Should().Be("system prompt");
        messages[1].GetProperty("role").GetString().Should().Be("user");
        messages[1].GetProperty("content").GetString().Should().Be("user prompt");

        var format = root.GetProperty("format");
        format.ValueKind.Should().Be(JsonValueKind.Object);
        format.GetProperty("type").GetString().Should().Be("object");

        var opts = root.GetProperty("options");
        opts.GetProperty("temperature").GetDouble().Should().BeApproximately(0.15, 1e-6);
        opts.GetProperty("top_p").GetDouble().Should().BeApproximately(0.8, 1e-6);
        opts.GetProperty("top_k").GetInt32().Should().Be(20);
        opts.GetProperty("presence_penalty").GetDouble().Should().BeApproximately(0.0, 1e-6);
        opts.GetProperty("num_ctx").GetInt32().Should().Be(8192);
        opts.GetProperty("num_predict").GetInt32().Should().Be(2048);
    }

    [Fact]
    public async Task CompleteAsync_WithProseProfile_OmitsFormatAndSetsPresencePenaltyToOne()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"message":{"role":"assistant","content":"Готовый ответ"}}""",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var router = new OllamaChatRouter(httpClient);
        var proseProfile = router.Describe("prose");
        proseProfile.PresencePenalty.Should().Be(1.0);
        proseProfile.Temperature.Should().BeApproximately(0.7, 1e-6);

        var chat = router.Resolve("prose");
        var text = await chat.CompleteAsync("sys", "usr");

        text.Should().Be("Готовый ответ");
        handler.Requests.Should().ContainSingle();
        var recorded = handler.Requests[0];
        recorded.Method.Should().Be(HttpMethod.Post);
        recorded.PathAndQuery.Should().Be("/api/chat");

        using var bodyDoc = JsonDocument.Parse(recorded.Body);
        var root = bodyDoc.RootElement;
        root.GetProperty("stream").GetBoolean().Should().BeFalse();
        root.TryGetProperty("format", out _).Should().BeFalse();

        var opts = root.GetProperty("options");
        opts.GetProperty("temperature").GetDouble().Should().BeApproximately(0.7, 1e-6);
        opts.GetProperty("top_p").GetDouble().Should().BeApproximately(0.8, 1e-6);
        opts.GetProperty("top_k").GetInt32().Should().Be(20);
        opts.GetProperty("presence_penalty").GetDouble().Should().BeApproximately(1.0, 1e-6);
        opts.GetProperty("num_ctx").GetInt32().Should().Be(8192);
        opts.GetProperty("num_predict").GetInt32().Should().Be(2048);
    }

    [Fact]
    public async Task ExtractAsync_WhenModelReturnsInvalidJson_ReturnsDefaultWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"message":{"role":"assistant","content":"{broken json"}}""",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var chat = new OllamaChatRouter(httpClient).Resolve("extract");

        var result = await chat.ExtractAsync<ExtractedNote>(
            "sys",
            "usr",
            """{"type":"object","properties":{"title":{"type":"string"}}}""");

        result.Should().BeNull();
    }

    [Fact]
    public void Router_UnknownProfile_ThrowsArgumentException()
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var router = new OllamaChatRouter(httpClient);

        Action act = () => router.Resolve("creative");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task EmbedQueryAsync_SendsExactPayloadWithoutPrefixAndL2NormalizesVector()
    {
        var rawVector = Enumerable.Range(1, 1024).Select(i => (float)(i % 13 + 1)).ToArray();
        var embedResponse = JsonSerializer.Serialize(new { embeddings = new[] { rawVector } });

        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(embedResponse, Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var client = new OllamaEmbeddingClient(httpClient, new OllamaOptions());

        client.ModelId.Should().Be("bge-m3");
        client.Dimensions.Should().Be(1024);
        client.QueryPrefix.Should().BeEmpty();

        var vector = await client.EmbedQueryAsync("тест secondbrain");

        vector.Length.Should().Be(1024);
        var norm = MathF.Sqrt(vector.Sum(static x => x * x));
        norm.Should().BeApproximately(1.0f, 1e-4f);

        handler.Requests.Should().ContainSingle();
        var recorded = handler.Requests[0];
        recorded.Method.Should().Be(HttpMethod.Post);
        recorded.PathAndQuery.Should().Be("/api/embed");

        using var bodyDoc = JsonDocument.Parse(recorded.Body);
        var root = bodyDoc.RootElement;
        root.GetProperty("model").GetString().Should().Be("bge-m3");
        root.GetProperty("truncate").GetBoolean().Should().BeFalse();
        root.GetProperty("keep_alive").GetString().Should().Be("10m");
        root.GetProperty("options").GetProperty("num_gpu").GetInt32().Should().Be(0);

        var input = root.GetProperty("input");
        input.GetArrayLength().Should().Be(1);
        input[0].GetString().Should().Be("тест secondbrain");
    }

    [Fact]
    public async Task EmbedDocumentsAsync_SplitsIntoBatchesByEmbedBatchSize()
    {
        var batchSizes = new List<int>();
        var handler = new FakeHttpMessageHandler((_, body) =>
        {
            using var doc = JsonDocument.Parse(body);
            var count = doc.RootElement.GetProperty("input").GetArrayLength();
            batchSizes.Add(count);

            var vectors = Enumerable.Range(0, count)
                .Select(_ => CreateUniformVector(1024, 3.0f))
                .ToArray();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { embeddings = vectors }), Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var client = new OllamaEmbeddingClient(httpClient, new OllamaOptions(EmbedBatchSize: 2));

        var vectors = await client.EmbedDocumentsAsync(["a", "b", "c", "d", "e"]);

        batchSizes.Should().Equal(2, 2, 1);
        vectors.Should().HaveCount(5);
        foreach (var vec in vectors)
        {
            vec.Length.Should().Be(1024);
            MathF.Sqrt(vec.Sum(static x => x * x)).Should().BeApproximately(1.0f, 1e-4f);
        }
    }

    [Fact]
    public async Task EmbedDocumentsAsync_WhenDimensionIsNot1024_ThrowsInvalidOperationException()
    {
        var wrongDimVector = CreateUniformVector(768, 1.0f);
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { embeddings = new[] { wrongDimVector } }),
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var client = new OllamaEmbeddingClient(httpClient);

        Func<Task> act = () => client.EmbedDocumentsAsync(["hello"]);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*1024*768*");
    }

    [Fact]
    public async Task EmbedDocumentsAsync_OnHttp400Truncation_ThrowsInvalidOperationExceptionWithTruncationMessage()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"input length exceeds the context length"}""", Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var client = new OllamaEmbeddingClient(httpClient);

        Func<Task> act = () => client.EmbedDocumentsAsync(["very long text"]);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*truncation*");
    }

    [Fact]
    public async Task EmbedDocumentsAsync_WhenVectorIsZero_ThrowsInvalidOperationException()
    {
        var zeroVector = new float[1024];
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { embeddings = new[] { zeroVector } }),
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var client = new OllamaEmbeddingClient(httpClient);

        Func<Task> act = () => client.EmbedDocumentsAsync(["zero"]);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RecordingAndReplayHandlers_RecordToDiskAndReplayWithoutNetwork()
    {
        var cassetteDir = CreateTempDir();
        var rawVector = CreateUniformVector(1024, 5.0f);
        var embedResponse = JsonSerializer.Serialize(new { embeddings = new[] { rawVector } });
        var chatResponse = JsonSerializer.Serialize(new
        {
            message = new
            {
                role = "assistant",
                content = """{"title":"Offline","tags":["cassette"]}"""
            }
        });

        var networkHits = 0;
        var fakeNetwork = new FakeHttpMessageHandler((req, _) =>
        {
            networkHits++;
            var path = req.RequestUri!.PathAndQuery;
            return path switch
            {
                "/api/chat" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(chatResponse, Encoding.UTF8, "application/json")
                },
                "/api/embed" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(embedResponse, Encoding.UTF8, "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

        const string schema = """{"type":"object","properties":{"title":{"type":"string"},"tags":{"type":"array","items":{"type":"string"}}}}""";

        ExtractedNote? recordedChat;
        float[] recordedEmbed;
        using (var recordingHandler = new RecordingHandler(fakeNetwork, cassetteDir))
        using (var recordHttp = new HttpClient(recordingHandler))
        {
            var chat = new OllamaChatRouter(recordHttp).Resolve("extract");
            var embed = new OllamaEmbeddingClient(recordHttp);

            recordedChat = await chat.ExtractAsync<ExtractedNote>("sys", "record me", schema);
            recordedEmbed = await embed.EmbedQueryAsync("replay query");
        }

        networkHits.Should().Be(2);
        var cassetteFiles = Directory.GetFiles(cassetteDir, "*.json");
        cassetteFiles.Should().HaveCount(2);
        foreach (var file in cassetteFiles)
        {
            Path.GetFileNameWithoutExtension(file).Should().HaveLength(16);
        }

        using var replayHandler = new ReplayHandler(cassetteDir);
        using var replayHttp = new HttpClient(replayHandler);
        var replayChatClient = new OllamaChatRouter(replayHttp).Resolve("extract");
        var replayEmbedClient = new OllamaEmbeddingClient(replayHttp);

        var replayedChat = await replayChatClient.ExtractAsync<ExtractedNote>("sys", "record me", schema);
        var replayedEmbed = await replayEmbedClient.EmbedQueryAsync("replay query");

        replayedChat.Should().NotBeNull();
        replayedChat!.Title.Should().Be(recordedChat!.Title);
        replayedChat.Tags.Should().Equal(recordedChat.Tags);
        replayedEmbed.Should().Equal(recordedEmbed);

        Func<Task> missingAct = () => replayEmbedClient.EmbedQueryAsync("unrecorded query");
        await missingAct.Should().ThrowAsync<FileNotFoundException>();
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"secondbrain-cassette-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static float[] CreateUniformVector(int dims, float value)
    {
        var vec = new float[dims];
        Array.Fill(vec, value);
        return vec;
    }

    private sealed record ExtractedNote(string Title, string[] Tags);

    private sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, string Body);

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : string.Empty;

            Requests.Add(new RecordedRequest(request.Method, request.RequestUri?.PathAndQuery ?? "/", body));
            return responder(request, body);
        }
    }
}