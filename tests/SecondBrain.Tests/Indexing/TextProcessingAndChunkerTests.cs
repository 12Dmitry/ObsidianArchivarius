using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using SecondBrain.Core.Model;
using SecondBrain.Core.Recipes;
using SecondBrain.Infrastructure.Indexing;
using Xunit;

namespace SecondBrain.Tests.Indexing;

public sealed class TextProcessingAndChunkerTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RuStemmer_And_RuNormalizer_Produce_Same_Stems_For_Inflected_Forms_And_Yo()
    {
        var norm1 = RuNormalizer.NormalizeForSearch("Архитектурное решение");
        var norm2 = RuNormalizer.NormalizeForSearch("архитектурных решений");

        norm1.Should().Be(norm2);
        RuNormalizer.TokenizeNormalized(norm1).Should().HaveCount(2);

        RuNormalizer.NormalizeForSearch("Ёлка").Should().Be(RuNormalizer.NormalizeForSearch("елка"));
        RuStemmer.Stem("ЁЛКА").Should().Be(RuStemmer.Stem("елки"));
    }

    [Theory]
    [InlineData("код")]
    [InlineData("сон")]
    [InlineData("дом")]
    [InlineData("дома")]
    [InlineData("моей")]
    [InlineData("оно")]
    public void RuStemmer_Never_Produces_Russian_Stem_Shorter_Than_Three_Characters(string word)
    {
        var stem = RuStemmer.Stem(word);

        stem.Length.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void RuStemmer_Matches_House_Word_Forms()
    {
        RuStemmer.Stem("дом").Should().Be("дом");
        RuStemmer.Stem("дома").Should().Be("дом");
        RuStemmer.Stem("домов").Should().Be("дом");
    }

    [Fact]
    public void RuNormalizer_Cleans_Markdown_Noise_And_Preserves_Mixed_Ru_En_Tokens()
    {
        var input = "Смотри [[Заметка|Алиас]], **жирный** `код` и C# / .NET 10!";
        var tokens = RuNormalizer.TokenizeNormalized(input);

        tokens.Should().Contain(RuStemmer.Stem("Смотри"));
        tokens.Should().Contain(RuStemmer.Stem("Заметка"));
        tokens.Should().Contain(RuStemmer.Stem("Алиас"));
        tokens.Should().Contain(RuStemmer.Stem("жирный"));
        tokens.Should().Contain("код");
        tokens.Should().Contain("c");
        tokens.Should().Contain("net");
        tokens.Should().Contain("10");

        var normalized = RuNormalizer.NormalizeForSearch(input);
        normalized.Should().NotContain("[");
        normalized.Should().NotContain("]");
        normalized.Should().NotContain("*");
        normalized.Should().NotContain("`");
        normalized.Should().NotContain("|");
    }

    [Fact]
    public void RuNormalizer_Returns_Empty_For_Null_Or_Punctuation_Only()
    {
        RuNormalizer.NormalizeForSearch(null).Should().BeEmpty();
        RuNormalizer.NormalizeForSearch("   ").Should().BeEmpty();
        RuNormalizer.NormalizeForSearch("[[ ## ** `` --- ").Should().BeEmpty();
        RuNormalizer.TokenizeNormalized("...").Should().BeEmpty();
    }

    [Fact]
    public void MarkdownChunker_Computes_Exact_ChunkId_And_Excludes_Tags_From_EmbedText()
    {
        var doc = new NoteDocument(
            @"./40_Insights\storage-note.md",
            new NoteMetadata
            {
                Id = "ins-1",
                Title = "Выбор хранилища",
                Type = NoteType.Insight,
                Tags = ["secret-tag-one", "sqlite-tag"],
                Created = FixedTimestamp,
                Updated = FixedTimestamp
            },
            "## Хранилище\n\nИспользуем локальный индекс без внешних зависимостей.\n",
            "hash",
            FixedTimestamp);

        var chunks = MarkdownChunker.Chunk(doc);

        chunks.Should().ContainSingle();
        var chunk = chunks[0];

        chunk.Path.Should().Be("40_Insights/storage-note.md");
        var expectedId = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{chunk.Path}#{chunk.Anchor}")));
        chunk.ChunkId.Should().Be(expectedId);

        chunk.EmbedText.Should().Contain("Выбор хранилища");
        chunk.EmbedText.Should().Contain("Хранилище");
        chunk.EmbedText.Should().Contain("Используем локальный индекс");
        chunk.EmbedText.Should().NotContain("secret-tag-one");
        chunk.EmbedText.Should().NotContain("sqlite-tag");

        // Ablation helper variants
        MarkdownChunker.BuildEmbedText("Title", "H1 > H2", "Body", doc.Metadata.Tags, MarkdownChunker.VariantBodyOnly)
            .Should().Be("Body");
        MarkdownChunker.BuildEmbedText("Title", "", "Body", doc.Metadata.Tags, MarkdownChunker.VariantTitleHeader)
            .Should().Be("Title\nBody");
        MarkdownChunker.BuildEmbedText("Title", "H1", "Body", doc.Metadata.Tags, MarkdownChunker.VariantTitleHeaderTags)
            .Should().Contain("secret-tag-one").And.Contain("sqlite-tag");
    }

    [Fact]
    public void MarkdownChunker_ByHeader_Tracks_Breadcrumbs_Ignores_CodeFence_Hashes_And_Disambiguates_Duplicate_Headers()
    {
        var body = """
            # Архитектура

            Общее описание системы.

            ## Хранилище

            Описание файлового хранилища.

            ```bash
            # Not a markdown header inside code fence
            ## Also not a header
            echo "hello"
            ```

            ### SQLite

            Таблица полнотекстового поиска.

            ## Итоги

            Первые итоги по хранилищу.

            ## Итоги

            Вторые итоги по производительности.
            """;

        var doc = CreateDoc("10_Projects/arch.md", "Архитектура системы", NoteType.Project, body);

        var chunks = MarkdownChunker.Chunk(doc, new ChunkPolicy("by_header", Size: 1100, Overlap: 150));

        chunks.Should().HaveCount(5);
        chunks.Select(c => c.HeaderBreadcrumb).Should().Equal(
            "Архитектура",
            "Архитектура > Хранилище",
            "Архитектура > Хранилище > SQLite",
            "Архитектура > Итоги",
            "Архитектура > Итоги");

        // Fenced code block stayed inside Хранилище chunk
        chunks[1].Body.Should().Contain("# Not a markdown header inside code fence");

        // Two identical "## Итоги" headings must have distinct Anchor and ChunkId
        chunks[3].Anchor.Should().NotBe(chunks[4].Anchor);
        chunks[3].ChunkId.Should().NotBe(chunks[4].ChunkId);
    }

    [Fact]
    public void MarkdownChunker_ByHeader_Splits_Long_Section_With_Overlap_And_Unique_ChunkIds()
    {
        var sentences = Enumerable.Range(1, 90)
            .Select(i => $"Предложение номер {i} описывает архитектурное решение для локальной базы знаний.");
        var longBody = "## Длинная секция\n\n" + string.Join(" ", sentences) + "\n";

        longBody.Length.Should().BeGreaterThan(2500);

        var doc = CreateDoc("notes/long.md", "Длинная заметка", NoteType.Insight, longBody);
        var chunks = MarkdownChunker.Chunk(doc, new ChunkPolicy("by_header", Size: 1100, Overlap: 150));

        chunks.Count.Should().BeGreaterThanOrEqualTo(3);
        chunks.Should().OnlyContain(c => c.Body.Length <= 1100);
        chunks.Should().OnlyContain(c => c.HeaderBreadcrumb == "Длинная секция");
        chunks.Select(c => c.Anchor).Should().OnlyHaveUniqueItems();
        chunks.Select(c => c.ChunkId).Should().OnlyHaveUniqueItems();

        // Verify actual overlap between consecutive windows
        var tailOfFirst = chunks[0].Body[^40..];
        chunks[1].Body.Should().Contain(tailOfFirst);
    }

    [Fact]
    public void MarkdownChunker_ByEntry_Extracts_Diary_Entry_Anchors_And_Unique_ChunkIds()
    {
        var diaryBody = """
            ## 09:15 — Утро
            Составил план на день и проверил задачи. ^e-0915

            ## 14:30 — День
            Провели встречу по архитектуре индексатора. ^e-1430

            ## 21:00 — Вечер
            Зафиксировал итоги и обновил журнал. ^e-2100
            """;

        var doc = CreateDoc("50_Diary/2026-11-21.md", "2026-11-21", NoteType.Diary, diaryBody);
        var chunks = MarkdownChunker.Chunk(doc, new ChunkPolicy("by_entry", Size: 1100, Overlap: 150));

        chunks.Should().HaveCount(3);
        chunks.Select(c => c.Anchor).Should().Equal("e-0915", "e-1430", "e-2100");
        chunks.Select(c => c.ChunkId).Should().OnlyHaveUniqueItems();

        foreach (var chunk in chunks)
        {
            var expectedId = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes($"50_Diary/2026-11-21.md#{chunk.Anchor}")));
            chunk.ChunkId.Should().Be(expectedId);
        }
    }

    [Fact]
    public void MarkdownChunker_Returns_Empty_For_Empty_Or_Whitespace_Body()
    {
        var emptyDoc = CreateDoc("notes/empty.md", "Empty", NoteType.Fleeting, "   \r\n\t\n  ");

        MarkdownChunker.Chunk(emptyDoc).Should().BeEmpty();
        MarkdownChunker.Chunk(emptyDoc, new ChunkPolicy("by_entry")).Should().BeEmpty();
    }

    private static NoteDocument CreateDoc(string path, string title, NoteType type, string body)
    {
        return new NoteDocument(
            path,
            new NoteMetadata
            {
                Id = $"id-{Guid.NewGuid():N}",
                Title = title,
                Type = type,
                Created = FixedTimestamp,
                Updated = FixedTimestamp
            },
            body,
            string.Empty,
            FixedTimestamp);
    }
}