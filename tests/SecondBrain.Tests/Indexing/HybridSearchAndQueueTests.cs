using FluentAssertions;
using Microsoft.Data.Sqlite;
using NSubstitute;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;
using SecondBrain.Infrastructure.Graph;
using SecondBrain.Infrastructure.Indexing;
using Xunit;

namespace SecondBrain.Tests.Indexing;

public sealed class HybridSearchAndQueueTests : IDisposable
{
    private const int VectorDimensions = 1024;
    private static readonly DateTimeOffset FixedNow = new(2026, 5, 10, 14, 30, 0, TimeSpan.Zero);

    private readonly string _tempRoot;
    private readonly string _vaultRoot;
    private readonly string _dbPath;
    private readonly FixedTimeProvider _timeProvider;
    private readonly InMemoryVaultStore _vaultStore;

    public HybridSearchAndQueueTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"secondbrain-hybrid-tests-{Guid.NewGuid():N}");
        _vaultRoot = Path.Combine(_tempRoot, "vault");
        _dbPath = Path.Combine(_tempRoot, "index.db");
        Directory.CreateDirectory(_vaultRoot);
        _timeProvider = new FixedTimeProvider(FixedNow);
        _vaultStore = new InMemoryVaultStore();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Fallback for transient OS antivirus/indexer locks.
            }
        }
    }

    [Fact]
    public async Task EmbeddingQueue_DrainAsync_Saves_Vectors_Handles_Chunk_Failure_And_Continues_On_Retry()
    {
        IEmbeddingClient embeddingClient = Substitute.For<IEmbeddingClient>();
        embeddingClient.ModelId.Returns("bge-m3");
        embeddingClient.Dimensions.Returns(VectorDimensions);

        bool failBadChunk = true;
        embeddingClient.EmbedDocumentsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                IReadOnlyList<string> texts = callInfo.ArgAt<IReadOnlyList<string>>(0);
                if (failBadChunk && texts.Any(t => t.Contains("BAD_OVERLONG_MARKER", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("Context length 8192 exceeded.");
                }

                IReadOnlyList<float[]> vectors = texts
                    .Select(_ => CreateUnitBasisVector(0))
                    .ToArray();
                return Task.FromResult(vectors);
            });

        using var index = new SqliteVaultIndex(_vaultRoot, _vaultStore, _timeProvider, _dbPath, embeddingClient);
        using var queue = new SqliteEmbeddingQueue(_dbPath, embeddingClient, _timeProvider);

        await index.UpsertAsync(CreateNote("notes/good1.md", "Good One", "Первая корректная заметка о системах."));
        await index.UpsertAsync(CreateNote("notes/bad.md", "Bad Chunk", "Текст с проблемой BAD_OVERLONG_MARKER внутри."));
        await index.UpsertAsync(CreateNote("notes/good2.md", "Good Two", "Вторая корректная заметка о поиске."));

        EmbedProgress firstRun = await queue.DrainAsync(batchSize: 10, maxDegreeOfParallelism: 1);

        firstRun.Done.Should().Be(2);
        firstRun.Failed.Should().Be(1);
        firstRun.Remaining.Should().Be(1);

        IndexStats statsAfterFirst = await index.GetStatsAsync();
        statsAfterFirst.Vectors.Should().Be(2);
        statsAfterFirst.EmbeddingModel.Should().Be("bge-m3");
        statsAfterFirst.Dimensions.Should().Be(VectorDimensions);

        (string Status, int Attempts, string? LastError) badState = await ReadQueueRowAsync("notes/bad.md");
        badState.Status.Should().Be("failed");
        badState.Attempts.Should().Be(1);
        badState.LastError.Should().Contain("8192");

        // Second attempt: bad chunk recovers and a newly added chunk is processed as well.
        failBadChunk = false;
        await index.UpsertAsync(CreateNote("notes/good3.md", "Good Three", "Третья заметка добавлена позже."));

        EmbedProgress secondRun = await queue.DrainAsync(batchSize: 10, maxDegreeOfParallelism: 1);

        secondRun.Done.Should().Be(2);
        secondRun.Failed.Should().Be(0);
        secondRun.Remaining.Should().Be(0);

        IndexStats statsAfterSecond = await index.GetStatsAsync();
        statsAfterSecond.Vectors.Should().Be(4);
    }

    [Fact]
    public async Task HybridSearch_Finds_Synonym_Via_BruteForceCosine_And_Boosts_Dual_Matches_In_Rrf()
    {
        IEmbeddingClient embeddingClient = Substitute.For<IEmbeddingClient>();
        embeddingClient.ModelId.Returns("bge-m3");
        embeddingClient.Dimensions.Returns(VectorDimensions);

        embeddingClient.EmbedDocumentsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                IReadOnlyList<string> texts = callInfo.ArgAt<IReadOnlyList<string>>(0);
                var result = new List<float[]>(texts.Count);
                foreach (string text in texts)
                {
                    if (text.Contains("Контейнеризация", StringComparison.Ordinal))
                    {
                        result.Add(CreateUnitBasisVector(0));
                    }
                    else if (text.Contains("Кулинария", StringComparison.Ordinal))
                    {
                        result.Add(CreateUnitBasisVector(1));
                    }
                    else
                    {
                        result.Add(CreateUnitBasisVector(2));
                    }
                }

                return Task.FromResult<IReadOnlyList<float[]>>(result);
            });

        embeddingClient.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                string queryText = callInfo.ArgAt<string>(0);
                if (queryText.Contains("docker", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(CreateUnitBasisVector(0));
                }

                return Task.FromResult(CreateUnitBasisVector(0));
            });

        using var index = new SqliteVaultIndex(_vaultRoot, _vaultStore, _timeProvider, _dbPath, embeddingClient);
        using var queue = new SqliteEmbeddingQueue(_dbPath, embeddingClient, _timeProvider);

        await index.UpsertAsync(CreateNote(
            "notes/containers.md",
            "Изоляция процессов",
            "Контейнеризация позволяет запускать сервисы в изолированных пространствах имён."));
        await index.UpsertAsync(CreateNote(
            "notes/cooking.md",
            "Домашняя кухня",
            "Кулинария требует точного соблюдения температуры приготовления."));
        await index.UpsertAsync(CreateNote(
            "notes/docker-cli.md",
            "Шпаргалка docker",
            "Команда docker ps показывает список запущенных процессов."));

        await queue.DrainAsync(batchSize: 8, maxDegreeOfParallelism: 1);

        // 1. Synonym query with 0 lexical overlap for containers.md ("docker" is NOT in containers.md)
        (SearchResult synonymResult, SearchExplain synonymExplain) = await index.SearchDetailedAsync(
            new SearchQuery("docker", Limit: 5),
            useLexical: false,
            useVector: true);

        synonymExplain.VectorUsed.Should().BeTrue();
        synonymResult.Timing.VectorMs.Should().BeGreaterThanOrEqualTo(0.0);
        synonymResult.Hits.First().Path.Should().Be("notes/containers.md");

        // 2. Hybrid query where lexical-only note has 0 lexical overlap on containers.md,
        //    proving vector fallback retrieves synonym note alongside lexical match via RrfMerger.
        (SearchResult hybridResult, SearchExplain hybridExplain) = await index.SearchDetailedAsync(
            new SearchQuery("docker", Limit: 5),
            useLexical: true,
            useVector: true);

        hybridExplain.VectorUsed.Should().BeTrue();
        hybridResult.Hits.Select(h => h.Path).Should().Contain(new[] { "notes/containers.md", "notes/docker-cli.md" });

        // 3. Lexical-only fallback when chunk_vectors is empty keeps VectorUsed == false.
        using var lexicalOnlyIndex = new SqliteVaultIndex(_vaultRoot, _vaultStore, _timeProvider, _dbPath, embeddingClient: null);
        (SearchResult lexResult, SearchExplain lexExplain) = await lexicalOnlyIndex.SearchDetailedAsync(
            new SearchQuery("docker", Limit: 5));

        lexExplain.VectorUsed.Should().BeFalse();
        lexResult.Hits.Select(h => h.Path).Should().Contain("notes/docker-cli.md");
    }

    [Fact]
    public async Task GraphExpansion_And_SqliteMapGraph_Find_OneHop_Neighbors_Membership_And_SyncPlan()
    {
        IEmbeddingClient embeddingClient = Substitute.For<IEmbeddingClient>();
        embeddingClient.ModelId.Returns("bge-m3");
        embeddingClient.Dimensions.Returns(VectorDimensions);

        NoteDocument mapNote = CreateNote(
            "maps/architecture.md",
            "Карта архитектуры",
            "Обзор ключевых решений.",
            type: NoteType.Map,
            tags: new[] { "architecture", "backend" });

        NoteDocument directNote = CreateNote(
            "notes/overview.md",
            "Архитектурный обзор",
            "Платформа использует модульный монолит и индекс SQLite.",
            tags: new[] { "architecture" },
            related: new[] { "notes/adr-01.md" },
            up: new[] { "maps/architecture.md" });

        NoteDocument neighborNote = CreateNote(
            "notes/adr-01.md",
            "ADR-01 Хранилище",
            "Выбор встраиваемой базы данных для локального кэша.",
            tags: new[] { "storage", "backend" });

        _vaultStore.Seed(mapNote);
        _vaultStore.Seed(directNote);
        _vaultStore.Seed(neighborNote);

        using var index = new SqliteVaultIndex(_vaultRoot, _vaultStore, _timeProvider, _dbPath, embeddingClient);
        await index.UpsertAsync(mapNote);
        await index.UpsertAsync(directNote);
        await index.UpsertAsync(neighborNote);

        var graph = new SqliteMapGraph(_dbPath, _vaultStore);

        // 1. ExpandAsync 1-hop BFS
        IReadOnlyList<GraphEdge> edges = await graph.ExpandAsync(
            new[] { "notes/overview.md" },
            hops: 1,
            limit: 4);

        edges.Should().Contain(e => e.Src == "notes/overview.md" && e.Dst == "notes/adr-01.md");
        edges.Should().Contain(e => e.Src == "notes/overview.md" && e.Dst == "maps/architecture.md");
        edges.Should().BeInDescendingOrder(e => e.Weight);

        // 2. ProposeMembershipAsync: explicit_up == 1.0, tag_rule == 0.5 + 0.1 * count
        IReadOnlyList<MapCandidate> directMembership = await graph.ProposeMembershipAsync("notes/overview.md");
        directMembership.Should().ContainSingle(c =>
            c.MapPath == "maps/architecture.md" &&
            c.Score == 1.0 &&
            c.Reason == "explicit_up");

        IReadOnlyList<MapCandidate> tagMembership = await graph.ProposeMembershipAsync("notes/adr-01.md");
        tagMembership.Should().ContainSingle(c =>
            c.MapPath == "maps/architecture.md" &&
            Math.Abs(c.Score - 0.6) < 1e-9 &&
            c.Reason == "tag_rule");

        // 3. PlanMapSyncAsync: generates replace_managed_block for brain:links
        WritePlan syncPlan = await graph.PlanMapSyncAsync("maps/architecture.md");
        syncPlan.Operations.Should().ContainSingle();
        WriteOperation op = syncPlan.Operations[0];
        op.Kind.Should().Be("replace_managed_block");
        op.Path.Should().Be("maps/architecture.md");
        op.ExpectedHash.Should().Be(mapNote.ContentHash);
        op.Payload.Should().Contain("<!-- brain:links:start -->");
        op.Payload.Should().Contain("[[notes/overview.md|Архитектурный обзор]]");
        op.Payload.Should().Contain("<!-- brain:links:end -->");

        // 4. SearchDetailedAsync with useGraph: true adds 1-hop neighbor without displacing direct hit
        (SearchResult graphResult, _) = await index.SearchDetailedAsync(
            new SearchQuery("монолит", Limit: 5),
            useLexical: true,
            useVector: false,
            useGraph: true);

        graphResult.Hits.Select(h => h.Path).Should().ContainInOrder("notes/overview.md", "notes/adr-01.md");
    }

    [Fact]
    public async Task RelaxationLadder_Relaxes_In_Strict_Order_Status_Type_Tags_Prefix_When_Below_MinHits()
    {
        using var index = new SqliteVaultIndex(_vaultRoot, _vaultStore, _timeProvider, _dbPath);

        await index.UpsertAsync(CreateNote(
            "projects/p1.md",
            "План 1",
            "Общий план развития платформы.",
            type: NoteType.Project,
            status: NoteStatus.Active,
            tags: new[] { "roadmap" }));

        await index.UpsertAsync(CreateNote(
            "projects/p2.md",
            "План 2",
            "Общий план развития поиска.",
            type: NoteType.Project,
            status: NoteStatus.Draft,
            tags: new[] { "roadmap" }));

        await index.UpsertAsync(CreateNote(
            "projects/p3.md",
            "План 3",
            "Общий план развития графа.",
            type: NoteType.Insight,
            status: NoteStatus.Draft,
            tags: new[] { "roadmap" }));

        await index.UpsertAsync(CreateNote(
            "projects/p4.md",
            "План 4",
            "Общий план развития очереди.",
            type: NoteType.Insight,
            status: NoteStatus.Draft,
            tags: new[] { "other" }));

        await index.UpsertAsync(CreateNote(
            "archive/p5.md",
            "План 5",
            "Общий план развития архива.",
            type: NoteType.Insight,
            status: NoteStatus.Draft,
            tags: new[] { "other" }));

        var strictFiltersAll = new SearchFilters(
            Tags: new[] { "roadmap" },
            Type: "project",
            Status: "active",
            PathPrefix: "projects/",
            Strict: true,
            MinHits: 5);

        (SearchResult fullResult, SearchExplain fullExplain) = await index.SearchDetailedAsync(
            new SearchQuery("план развития", Limit: 10),
            filters: strictFiltersAll,
            useLexical: true,
            useVector: false);

        fullExplain.CandidatesBeforeFilters.Should().Be(5);
        fullResult.Hits.Should().HaveCount(5);
        fullExplain.Applied.Select(s => s.Order).Should().Equal(1, 2, 3, 4);
        fullExplain.Applied.Select(s => s.Field).Should().Equal("status", "type", "tags", "prefix");

        var strictFiltersMin3 = strictFiltersAll with { MinHits = 3 };
        (SearchResult partialResult, SearchExplain partialExplain) = await index.SearchDetailedAsync(
            new SearchQuery("план развития", Limit: 10),
            filters: strictFiltersMin3,
            useLexical: true,
            useVector: false);

        partialResult.Hits.Should().HaveCount(3);
        partialExplain.Applied.Select(s => s.Field).Should().Equal("status", "type");
    }

    private async Task<(string Status, int Attempts, string? LastError)> ReadQueueRowAsync(string notePath)
    {
        string cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT eq.status, eq.attempts, eq.last_error
            FROM embedding_queue eq
            INNER JOIN chunks c ON c.chunk_id = eq.chunk_id
            WHERE c.path = @path
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@path", notePath);

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static NoteDocument CreateNote(
        string path,
        string title,
        string body,
        NoteType type = NoteType.Insight,
        NoteStatus status = NoteStatus.Active,
        string[]? tags = null,
        string[]? related = null,
        string[]? up = null)
    {
        var metadata = new NoteMetadata
        {
            Id = path,
            Title = title,
            Type = type,
            Status = status,
            Tags = tags ?? Array.Empty<string>(),
            Related = related ?? Array.Empty<string>(),
            Up = up ?? Array.Empty<string>(),
            Created = FixedNow,
            Updated = FixedNow
        };

        return new NoteDocument(
            Path: path,
            Metadata: metadata,
            Body: body,
            ContentHash: $"hash:{path}",
            FileModifiedAt: FixedNow);
    }

    private static float[] CreateUnitBasisVector(int activeIndex)
    {
        var vector = new float[VectorDimensions];
        vector[activeIndex] = 1.0f;
        return vector;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class InMemoryVaultStore : IVaultStore
    {
        private readonly Dictionary<string, NoteDocument> _notes = new(StringComparer.Ordinal);

        public void Seed(NoteDocument doc)
        {
            _notes[doc.Path] = doc;
        }

        public Task<IReadOnlyList<VaultEntry>> ListAsync(string? prefix = null, CancellationToken ct = default)
        {
            IReadOnlyList<VaultEntry> entries = _notes.Values
                .Where(n => string.IsNullOrEmpty(prefix) || n.Path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(n => new VaultEntry(n.Path, n.Metadata.Type, n.Metadata.Title, n.FileModifiedAt, n.Body.Length))
                .ToArray();
            return Task.FromResult(entries);
        }

        public Task<NoteDocument?> ReadAsync(string path, CancellationToken ct = default)
        {
            _notes.TryGetValue(path, out NoteDocument? doc);
            return Task.FromResult(doc);
        }

        public Task<WriteResult> WriteAsync(NoteDocument doc, WriteOptions options, CancellationToken ct = default)
        {
            if (!options.DryRun)
            {
                _notes[doc.Path] = doc;
            }

            return Task.FromResult(new WriteResult(true, doc.ContentHash, null, null));
        }

        public Task<bool> DeleteAsync(string path, string expectedHash, CancellationToken ct = default)
        {
            return Task.FromResult(_notes.Remove(path));
        }
    }
}