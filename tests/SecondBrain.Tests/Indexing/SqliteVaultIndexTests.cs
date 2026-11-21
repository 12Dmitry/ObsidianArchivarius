using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Linking;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;
using SecondBrain.Infrastructure.Indexing;
using SecondBrain.Infrastructure.Vault;
using Xunit;

namespace SecondBrain.Tests.Indexing;

public sealed class SqliteVaultIndexTests : IDisposable
{
    private readonly string _vaultRoot;
    private readonly FixedTimeProvider _timeProvider;
    private readonly FileSystemVaultStore _vaultStore;
    private readonly SqliteVaultIndex _index;

    public SqliteVaultIndexTests()
    {
        _vaultRoot = Path.Combine(
            Path.GetTempPath(),
            $"secondbrain-idx-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_vaultRoot);

        _timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));
        _vaultStore = new FileSystemVaultStore(_vaultRoot, _timeProvider);
        _index = new SqliteVaultIndex(_vaultRoot, _vaultStore, _timeProvider);
    }

    public void Dispose()
    {
        _index.Dispose();

        if (Directory.Exists(_vaultRoot))
        {
            Directory.Delete(_vaultRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureInitializedAsync_CreatesSchemaAndEnablesWalMode()
    {
        await _index.EnsureInitializedAsync(CancellationToken.None);

        var dbPath = Path.Combine(_vaultRoot, ".brain", "index.db");
        File.Exists(dbPath).Should().BeTrue();

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(CancellationToken.None);

        await using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode;";
            var mode = (string?)await pragmaCmd.ExecuteScalarAsync(CancellationToken.None);
            mode.Should().NotBeNull();
            mode!.ToLowerInvariant().Should().Be("wal");
        }

        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using (var tablesCmd = connection.CreateCommand())
        {
            tablesCmd.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type IN ('table', 'index');
                """;
            await using var reader = await tablesCmd.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                tables.Add(reader.GetString(0));
            }
        }

        tables.Should().Contain([
            "notes",
            "chunks",
            "chunks_fts",
            "edges",
            "embedding_queue",
            "index_meta",
            "idx_chunks_path",
            "idx_notes_type"
        ]);
    }

    [Fact]
    public async Task UpsertAsync_PopulatesEdgesAndPendingEmbeddingQueue()
    {
        var relations = new[]
        {
            new ResolvedRelation(
                SrcPath: "decisions/cache-strategy.md",
                DstPath: "insights/perf-bench.md",
                Kind: RelationKind.Supports,
                Weight: 0.85,
                Origin: "llm",
                Reason: "benchmark supports cache choice")
        };

        var doc = CreateDocument(
            path: "./decisions\\cache-strategy.md",
            id: "dec-1",
            title: "Стратегия кэширования",
            type: NoteType.Decision,
            body: "# Выбор\nИспользуем двухуровневый кэш.",
            tags: ["arch", "cache"],
            related: ["insights/redis-notes.md"],
            up: ["maps/architecture.md"],
            relations: relations);

        await _index.UpsertAsync(doc, CancellationToken.None);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_vaultRoot, ".brain", "index.db"),
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(CancellationToken.None);

        var edges = new List<(string Src, string Dst, string Kind, double Weight, string Origin)>();
        await using (var edgesCmd = connection.CreateCommand())
        {
            edgesCmd.CommandText = """
                SELECT src_path, dst_path, kind, weight, origin
                FROM edges
                ORDER BY kind;
                """;
            await using var reader = await edgesCmd.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                edges.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDouble(3),
                    reader.GetString(4)));
            }
        }

        edges.Should().Contain([
            ("decisions/cache-strategy.md", "insights/redis-notes.md", "related", 1.0, "frontmatter"),
            ("decisions/cache-strategy.md", "insights/perf-bench.md", "supports", 0.85, "llm"),
            ("decisions/cache-strategy.md", "maps/architecture.md", "up", 1.0, "frontmatter")
        ]);

        await using var queueCmd = connection.CreateCommand();
        queueCmd.CommandText = "SELECT status, updated_at FROM embedding_queue;";
        await using var queueReader = await queueCmd.ExecuteReaderAsync(CancellationToken.None);
        (await queueReader.ReadAsync(CancellationToken.None)).Should().BeTrue();
        queueReader.GetString(0).Should().Be("pending");
        queueReader.GetString(1).Should().Be(_timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SearchAsync_MatchesRussianMorphologyAndYoNormalization()
    {
        var cacheDoc = CreateDocument(
            path: "decisions/caching.md",
            id: "dec-cache",
            title: "Кэш в сервисе",
            type: NoteType.Decision,
            body: "Принято архитектурное решение по кэшированию ответов API.",
            tags: ["backend"]);

        var yoDoc = CreateDocument(
            path: "notes/tree-yo.md",
            id: "note-yo",
            title: "Праздник",
            type: NoteType.Fleeting,
            body: "Новогодняя ёлка стоит в переговорке.");

        var yeDoc = CreateDocument(
            path: "notes/tree-ye.md",
            id: "note-ye",
            title: "Природа",
            type: NoteType.Fleeting,
            body: "Зеленая елка в лесу.");

        await _index.UpsertAsync(cacheDoc, CancellationToken.None);
        await _index.UpsertAsync(yoDoc, CancellationToken.None);
        await _index.UpsertAsync(yeDoc, CancellationToken.None);

        var morphResult = await _index.SearchAsync(
            new SearchQuery(Text: "архитектурных решений"),
            CancellationToken.None);
        morphResult.Hits.Should().ContainSingle(h => h.Path == "decisions/caching.md");

        var cacheResult = await _index.SearchAsync(
            new SearchQuery(Text: "кэширование"),
            CancellationToken.None);
        cacheResult.Hits.Should().ContainSingle(h => h.Path == "decisions/caching.md");

        var searchYeForYo = await _index.SearchAsync(
            new SearchQuery(Text: "елка"),
            CancellationToken.None);
        searchYeForYo.Hits.Select(static h => h.Path)
            .Should().Contain(["notes/tree-yo.md", "notes/tree-ye.md"]);

        var searchYoForYe = await _index.SearchAsync(
            new SearchQuery(Text: "ёлка"),
            CancellationToken.None);
        searchYoForYe.Hits.Select(static h => h.Path)
            .Should().Contain(["notes/tree-yo.md", "notes/tree-ye.md"]);
    }

    [Fact]
    public async Task SearchAsync_WithFts5SpecialCharacters_DoesNotThrowSyntaxError()
    {
        var doc = CreateDocument(
            path: "resources/cpp-notes.md",
            id: "res-cpp",
            title: "C++ и тесты",
            type: NoteType.Resource,
            body: "Интеграционный test для модуля foo bar и сервиса baz.");

        await _index.UpsertAsync(doc, CancellationToken.None);

        var result = await _index.SearchAsync(
            new SearchQuery(Text: "C++ / foo-bar: (test) OR \"baz\""),
            CancellationToken.None);

        result.Hits.Should().ContainSingle(h => h.Path == "resources/cpp-notes.md");
        result.Timing.VectorMs.Should().Be(0);
        result.Timing.TotalMs.Should().BeGreaterThanOrEqualTo(0);

        var punctuationOnly = await _index.SearchAsync(
            new SearchQuery(Text: "--- ::: *** () \"\""),
            CancellationToken.None);
        punctuationOnly.Hits.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WithShortQueryBelowThreeChars_UsesLikeFallback()
    {
        var aiDoc = CreateDocument(
            path: "insights/ai-agents.md",
            id: "ins-ai",
            title: "Агенты AI",
            type: NoteType.Insight,
            body: "Локальный оркестратор для ai инструментов.");

        var goDoc = CreateDocument(
            path: "resources/go-lang.md",
            id: "res-go",
            title: "Сервис на Go",
            type: NoteType.Resource,
            body: "Микросервис написан на языке Go для высокой нагрузки.");

        await _index.UpsertAsync(aiDoc, CancellationToken.None);
        await _index.UpsertAsync(goDoc, CancellationToken.None);

        var aiResult = await _index.SearchAsync(new SearchQuery(Text: "ai"), CancellationToken.None);
        aiResult.Hits.Should().Contain(h => h.Path == "insights/ai-agents.md");

        var goResult = await _index.SearchAsync(new SearchQuery(Text: "go"), CancellationToken.None);
        goResult.Hits.Should().Contain(h => h.Path == "resources/go-lang.md");
    }

    [Fact]
    public async Task SearchAsync_TreatsFiltersAsSoftBoostsNotHardWhereClauses()
    {
        const string sharedBody = "Единый текст про распределенное кэширование данных в кластере.";

        var unboostedDoc = CreateDocument(
            path: "insights/cluster-cache.md",
            id: "ins-1",
            title: "Заметка про кэширование",
            type: NoteType.Insight,
            body: sharedBody,
            tags: ["other"]);

        var boostedDoc = CreateDocument(
            path: "decisions/cluster-cache.md",
            id: "dec-1",
            title: "Заметка про кэширование",
            type: NoteType.Decision,
            body: sharedBody,
            tags: ["arch", "cache"]);

        await _index.UpsertAsync(unboostedDoc, CancellationToken.None);
        await _index.UpsertAsync(boostedDoc, CancellationToken.None);

        var result = await _index.SearchAsync(
            new SearchQuery(
                Text: "кэширование",
                Type: NoteType.Decision,
                Tags: ["arch", "cache"],
                PathPrefix: "decisions/"),
            CancellationToken.None);

        result.Hits.Should().HaveCount(2);
        result.Hits[0].Path.Should().Be("decisions/cluster-cache.md");
        result.Hits[1].Path.Should().Be("insights/cluster-cache.md");
        result.Hits[0].Score.Should().BeGreaterThan(result.Hits[1].Score);
    }

    [Fact]
    public async Task DeleteAsyncAndRebuildAsync_RemoveDeletedNotesAndSkipRawFilesWithoutFrontmatter()
    {
        var keepDoc = CreateDocument(
            path: "notes/keep.md",
            id: "keep-1",
            title: "Актуальный микросервис",
            type: NoteType.Resource,
            body: "Документация по развертыванию микросервиса.");

        var removeDoc = CreateDocument(
            path: "notes/remove.md",
            id: "rem-1",
            title: "Удаляемый микросервис",
            type: NoteType.Resource,
            body: "Устаревшая документация по развертыванию микросервиса.");

        await _index.UpsertAsync(keepDoc, CancellationToken.None);
        await _index.UpsertAsync(removeDoc, CancellationToken.None);

        await _index.DeleteAsync("notes/remove.md", CancellationToken.None);

        var afterDelete = await _index.SearchAsync(new SearchQuery(Text: "микросервис"), CancellationToken.None);
        afterDelete.Hits.Should().ContainSingle(h => h.Path == "notes/keep.md");

        var d1Doc = CreateDocument(
            path: "decisions/d1.md",
            id: "d-1",
            title: "Решение по очередям",
            type: NoteType.Decision,
            body: "# Очереди\nИспользуем RabbitMQ для фоновых задач.",
            tags: ["infra"]);

        var i1Doc = CreateDocument(
            path: "insights/i1.md",
            id: "i-1",
            title: "Инсайт по очередям",
            type: NoteType.Insight,
            body: "# Наблюдение\nБатчинг в RabbitMQ снижает задержки.",
            tags: ["infra"]);

        var d1Write = await _vaultStore.WriteAsync(d1Doc, new WriteOptions(Reason: "test"), CancellationToken.None);
        d1Write.Success.Should().BeTrue();

        var i1Write = await _vaultStore.WriteAsync(i1Doc, new WriteOptions(Reason: "test"), CancellationToken.None);
        i1Write.Success.Should().BeTrue();

        await WriteRawFileAsync(
            relativePath: "raw/no-frontmatter.md",
            content: "# Сырой файл без YAML frontmatter\nПросто текст.");

        var stats1 = await _index.RebuildAsync(CancellationToken.None);
        stats1.Chunks.Should().BeGreaterThanOrEqualTo(2);
        stats1.Vectors.Should().Be(0);
        stats1.EmbeddingModel.Should().Be("none");
        stats1.Dimensions.Should().Be(0);
        stats1.BuiltAt.Should().Be(_timeProvider.GetUtcNow());

        var afterRebuild1 = await _index.SearchAsync(new SearchQuery(Text: "RabbitMQ"), CancellationToken.None);
        afterRebuild1.Hits.Select(static h => h.Path)
            .Should().BeEquivalentTo(["decisions/d1.md", "insights/i1.md"]);

        File.Delete(Path.Combine(_vaultRoot, "insights", "i1.md"));
        _timeProvider.Advance(TimeSpan.FromMinutes(5));

        var stats2 = await _index.RebuildAsync(CancellationToken.None);
        stats2.BuiltAt.Should().Be(_timeProvider.GetUtcNow());

        var afterRebuild2 = await _index.SearchAsync(new SearchQuery(Text: "RabbitMQ"), CancellationToken.None);
        afterRebuild2.Hits.Should().ContainSingle(h => h.Path == "decisions/d1.md");

        var doctorStats = await _index.GetStatsAsync(CancellationToken.None);
        doctorStats.Should().Be(stats2);
    }

    [Fact]
    public async Task DiaryNote_ChunksByEntryAnchorAndIsSearchable()
    {
        var diaryDoc = CreateDocument(
            path: "diary/2026-11-21.md",
            id: "diary-2026-11-21",
            title: "Дневник 2026-11-21",
            type: NoteType.Diary,
            body: """
                  Утренняя пробежка и планирование спринта ^e-0930

                  Вечерний разбор распределенного кэширования в продакшене ^e-1845
                  """);

        await _index.UpsertAsync(diaryDoc, CancellationToken.None);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_vaultRoot, ".brain", "index.db"),
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(CancellationToken.None);

        var anchors = new List<string>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT anchor FROM chunks WHERE path = @path ORDER BY chunk_index;";
            cmd.Parameters.AddWithValue("@path", "diary/2026-11-21.md");
            await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                anchors.Add(reader.GetString(0));
            }
        }

        anchors.Should().Equal("e-0930", "e-1845");

        var searchEvening = await _index.SearchAsync(
            new SearchQuery(Text: "кэширования"),
            CancellationToken.None);
        searchEvening.Hits.Should().ContainSingle(h => h.Path == "diary/2026-11-21.md");
        searchEvening.Hits[0].Snippet.Should().Contain("кэширования");
    }

    private NoteDocument CreateDocument(
        string path,
        string id,
        string title,
        NoteType type,
        string body,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? related = null,
        IReadOnlyList<string>? up = null,
        IReadOnlyList<ResolvedRelation>? relations = null)
    {
        var now = _timeProvider.GetUtcNow();
        var metadata = new NoteMetadata
        {
            Id = id,
            Title = title,
            Type = type,
            Tags = tags ?? [],
            Related = related ?? [],
            Up = up ?? [],
            Relations = relations ?? [],
            Created = now,
            Updated = now
        };

        return new NoteDocument(
            Path: path,
            Metadata: metadata,
            Body: body,
            ContentHash: $"hash-{id}",
            FileModifiedAt: now);
    }

    private async Task WriteRawFileAsync(string relativePath, string content)
    {
        var fullPath = Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(fullPath, Encoding.UTF8.GetBytes(content), CancellationToken.None);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}