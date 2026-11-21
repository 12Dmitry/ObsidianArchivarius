using System.Collections.Concurrent;
using FluentAssertions;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;
using SecondBrain.Core.Recipes;
using SecondBrain.Infrastructure.Capture;
using SecondBrain.Infrastructure.Indexing;
using SecondBrain.Infrastructure.Vault;
using Xunit;

namespace SecondBrain.Tests.Capture;

public sealed class LocalCaptureServiceTests : IDisposable
{
    private readonly string _vaultRoot;
    private readonly MutableTimeProvider _timeProvider;
    private readonly FileSystemVaultStore _store;
    private readonly JsonlWriteJournal _journal;
    private readonly InMemoryTestVaultIndex _index;
    private readonly LocalCaptureService _service;

    public LocalCaptureServiceTests()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), $"secondbrain-capture-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_vaultRoot);

        _timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 11, 21, 16, 15, 0, TimeSpan.Zero));
        _store = new FileSystemVaultStore(_vaultRoot, _timeProvider);
        _journal = new JsonlWriteJournal(_vaultRoot, _timeProvider);
        _index = new InMemoryTestVaultIndex();
        _service = new LocalCaptureService(_store, _journal, _index, _timeProvider);
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultRoot))
        {
            Directory.Delete(_vaultRoot, recursive: true);
        }
    }

    [Fact]
    public void DirectiveParser_Parses_SingleLine_And_MultiLine_Directives_While_Preserving_Headings_And_Inline_Tags()
    {
        const string input = """
            >> folder: 40_Insights >> #Arch >> type: insight >> map: [[System]] >> alias: Кратко >> url: https://example.com
            >> tag: #arch >> tag: Storage
            >> alias: Короткое имя

            # Архитектура хранилища

            Обычный абзац с #встроенный_тег и ссылкой [[System]].
            """;

        var parsed = DirectiveParser.Parse(input);

        parsed.Directives.Folder.Should().Be("40_Insights");
        parsed.Directives.NoteType.Should().Be("insight");
        parsed.Directives.Tags.Should().Equal("arch", "storage");
        parsed.Directives.Maps.Should().Equal("[[System]]");
        parsed.Directives.Aliases.Should().Equal("Кратко", "Короткое имя");
        parsed.Directives.SourceUrl.Should().Be("https://example.com");

        parsed.CleanBody.Should().Contain("# Архитектура хранилища");
        parsed.CleanBody.Should().Contain("Обычный абзац с #встроенный_тег");
        parsed.CleanBody.Should().NotContain(">> folder:");
        parsed.CleanBody.Should().NotContain(">> type:");
        parsed.CleanBody.Should().NotContain(">> #Arch");
    }

    [Fact]
    public async Task CaptureAsync_Default_DryRun_Returns_Diff_Without_Writing_File_Or_Updating_Index()
    {
        const string raw = """
            >> type: decision >> #adr
            # Выбор движка индексации

            Используем SQLite FTS5 для локального поиска.
            """;

        var result = await _service.CaptureAsync(raw);

        result.Applied.Should().BeFalse();
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Path.Should().StartWith("80_Decisions/");
        result.Diff.Should().NotBeNullOrEmpty();
        result.Diff.Should().Contain("+++ b/80_Decisions/");

        (await _store.ReadAsync(result.Path)).Should().BeNull();
        (await _store.ListAsync()).Should().BeEmpty();

        var search = await _index.SearchAsync(new SearchQuery("индексации"));
        search.Hits.Should().BeEmpty();
    }

    [Fact]
    public async Task CaptureAsync_With_Apply_Creates_Unique_Slug_Files_In_Target_Folder_And_Indexes_Immediately()
    {
        const string rawFirst = """
            >> type: insight >> #Architecture >> #architecture
            # Локальный поиск по заметкам

            Быстрый полнотекстовый поиск без облачных моделей.
            """;

        var first = await _service.CaptureAsync(rawFirst, apply: true);

        first.Applied.Should().BeTrue();
        first.Success.Should().BeTrue();
        first.Path.Should().Be("40_Insights/lokalnyy-poisk-po-zametkam.md");
        first.Document.Metadata.Type.Should().Be(NoteType.Insight);
        first.Document.Metadata.Tags.Should().Equal("architecture");
        first.Document.ContentHash.Should().NotBeNullOrEmpty();

        var loadedFirst = await _store.ReadAsync(first.Path);
        loadedFirst.Should().NotBeNull();
        loadedFirst!.ContentHash.Should().Be(first.Document.ContentHash);

        // Advance time by 10 seconds and capture with the same title -> unique slug collision handling
        _timeProvider.Advance(TimeSpan.FromSeconds(10));

        const string rawSecond = """
            >> type: insight >> #architecture
            # Локальный поиск по заметкам

            Вторая заметка с таким же заголовком, но другим содержанием.
            """;

        var second = await _service.CaptureAsync(rawSecond, apply: true);

        second.Applied.Should().BeTrue();
        second.Success.Should().BeTrue();
        second.Path.Should().Be("40_Insights/lokalnyy-poisk-po-zametkam-2.md");
        second.Path.Should().NotBe(first.Path);
        second.Document.Metadata.Id.Should().NotBe(first.Document.Metadata.Id);

        var hits = await _index.SearchAsync(new SearchQuery("полнотекстовый поиск", Type: NoteType.Insight));
        hits.Hits.Should().Contain(h => h.Path == first.Path);

        var hitsSecond = await _index.SearchAsync(new SearchQuery("вторая заметка", Type: NoteType.Insight));
        hitsSecond.Hits.Should().Contain(h => h.Path == second.Path);
    }

    [Fact]
    public async Task AppendDiaryAsync_Adds_Two_Entries_In_Same_Minute_With_Unique_Anchors_And_Indexes_Both()
    {
        const string firstEntry = "Утренний обзор архитектуры хранения заметок.";
        const string secondEntry = "Вечерняя проверка атомарного журнала записи.";

        var r1 = await _service.AppendDiaryAsync(firstEntry);
        var r2 = await _service.AppendDiaryAsync(secondEntry);

        r1.Applied.Should().BeTrue();
        r1.Success.Should().BeTrue();
        r1.Path.Should().Be("20_Areas/Diary/2026-11-21.md");

        r2.Applied.Should().BeTrue();
        r2.Success.Should().BeTrue();
        r2.Path.Should().Be("20_Areas/Diary/2026-11-21.md");

        var diaryDoc = await _store.ReadAsync("20_Areas/Diary/2026-11-21.md");
        diaryDoc.Should().NotBeNull();
        diaryDoc!.Metadata.Type.Should().Be(NoteType.Diary);
        diaryDoc.Metadata.Usecase.Should().Be("diary");
        diaryDoc.Metadata.Id.Should().Be("diary-2026-11-21");
        diaryDoc.Body.Should().Contain("## 16:15 ^e-1615");
        diaryDoc.Body.Should().Contain("## 16:15 ^e-1615-2");

        var chunks = MarkdownChunker.Chunk(diaryDoc, new ChunkPolicy("by_entry"));
        chunks.Should().HaveCount(2);
        chunks.Select(c => c.Anchor).Should().Equal("e-1615", "e-1615-2");

        var searchFirst = await _index.SearchAsync(new SearchQuery("архитектуры хранения"));
        searchFirst.Hits.Should().Contain(h => h.Path == "20_Areas/Diary/2026-11-21.md" && h.Snippet.Contains("Утренний обзор"));

        var searchSecond = await _index.SearchAsync(new SearchQuery("атомарного журнала"));
        searchSecond.Hits.Should().Contain(h => h.Path == "20_Areas/Diary/2026-11-21.md" && h.Snippet.Contains("Вечерняя проверка"));
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class InMemoryTestVaultIndex : IVaultIndex
    {
        private readonly ConcurrentDictionary<string, IndexedNote> _notes = new(StringComparer.Ordinal);

        public Task EnsureInitializedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertAsync(NoteDocument doc, CancellationToken ct = default)
        {
            var policy = doc.Metadata.Type == NoteType.Diary
                ? new ChunkPolicy("by_entry")
                : new ChunkPolicy("by_header");

            var chunks = MarkdownChunker.Chunk(doc, policy);
            _notes[doc.Path] = new IndexedNote(doc, chunks);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            _notes.TryRemove(path, out _);
            return Task.CompletedTask;
        }

        public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
        {
            var queryTokens = RuNormalizer.TokenizeNormalized(query.Text);
            var hits = new List<SearchHit>();

            foreach (var indexed in _notes.Values)
            {
                var doc = indexed.Document;
                if (query.Type.HasValue && doc.Metadata.Type != query.Type.Value)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(query.PathPrefix) &&
                    !doc.Path.StartsWith(query.PathPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (query.Tags is { Length: > 0 } &&
                    !query.Tags.All(t => doc.Metadata.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (var chunk in indexed.Chunks)
                {
                    var haystack = $"{RuNormalizer.NormalizeForSearch(doc.Metadata.Title)} {chunk.BodyNorm}";
                    var score = queryTokens.Count(token => haystack.Contains(token, StringComparison.Ordinal));
                    if (score > 0)
                    {
                        hits.Add(new SearchHit(
                            doc.Path,
                            doc.Metadata.Title,
                            chunk.Body,
                            score,
                            chunk.HeaderBreadcrumb,
                            [.. doc.Metadata.Tags]));
                    }
                }
            }

            var ordered = hits
                .OrderByDescending(h => h.Score)
                .Take(query.Limit)
                .ToList();

            return Task.FromResult(new SearchResult(ordered, new SearchTiming(1.0, 0.0, 1.0)));
        }

        public Task<IndexStats> RebuildAsync(CancellationToken ct = default)
        {
            var totalChunks = _notes.Values.Sum(n => n.Chunks.Count);
            return Task.FromResult(new IndexStats(totalChunks, 0, DateTimeOffset.UnixEpoch, "none", 0));
        }

        private sealed record IndexedNote(NoteDocument Document, IReadOnlyList<NoteChunk> Chunks);
    }
}