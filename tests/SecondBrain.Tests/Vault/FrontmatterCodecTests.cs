using System.Globalization;
using SecondBrain.Core.Linking;
using SecondBrain.Core.Model;
using SecondBrain.Infrastructure.Vault;
using Xunit;

namespace SecondBrain.Tests.Infrastructure.Vault;

public sealed class FrontmatterCodecTests
{
    private static readonly DateTimeOffset FixedDate = new(2026, 11, 21, 3, 50, 0, TimeSpan.Zero);

    [Fact]
    public void GoldenRoundTrip_PreservesAllFieldsAndVerbatimBodyWithDashes()
    {
        var metadata = new NoteMetadata
        {
            Id = "note-golden-1",
            Title = "Архитектура .NET 10 & Obsidian Vault",
            Type = NoteType.Diary,
            Usecase = "concept",
            Status = NoteStatus.OnHold,
            Tags = ["arch", "dotnet", "csharp"],
            TagsProposed = ["performance"],
            Aliases = ["dot-net-ten", "архитектура"],
            Related = ["note-base", "note-extra"],
            Up = ["index-note"],
            Relations =
            [
                new ResolvedRelation("notes/a.md", "notes/b.md", RelationKind.PartOf, 0.85, "manual", "core component"),
                new ResolvedRelation("notes/a.md", "notes/c.md", RelationKind.DuplicateOf, 1.0, "llm", "exact duplicate")
            ],
            Summary = "Золотой тест полного цикла сериализации и десериализации",
            Lang = "ru",
            Created = FixedDate,
            Updated = FixedDate.AddHours(2),
            SchemaVersion = 2,
            Source = "manual",
            SourceUrl = "https://example.com/spec",
            ContentHash = "yaml-stored-hash",
            MentionsRaw = ["[[Unlinked Mention]]", "[[Another]]"],
            LinksOrigin = new Dictionary<string, string> { ["[[Target]]"] = "section-1" },
            Llm = new LlmProvenance
            {
                Model = "gpt-5-preview",
                Profile = "deep-research",
                PromptPack = "pack-v1",
                PromptHash = "phash123",
                RunId = "run-999",
                ExtractedAt = FixedDate.AddMinutes(30)
            },
            Privacy = new PrivacyInfo { CloudAllowed = true, Sensitivity = "restricted" },
            Processed = true,
            GeneratedNotes = ["notes/gen1.md"],
            Stub = true,
            ArchivedAt = FixedDate.AddDays(10),
            ReviewDate = FixedDate.AddDays(30),
            ReviewedAt = FixedDate.AddDays(5),
            Confidence = 0.99,
            Slots = new Dictionary<string, object?> { ["tier"] = "production", ["priority"] = 1L },
            DecisionLog = new Dictionary<string, object?> { ["approved"] = true },
            Project = new Dictionary<string, object?> { ["code"] = "SB-2026" },
            Extra = new Dictionary<string, object?> { ["flag"] = true, ["score"] = 42L },
            UnknownKeys = new Dictionary<string, object?>
            {
                ["zeta_key"] = "last",
                ["alpha_key"] = "first",
                ["quoted_bool"] = "true",
                ["actual_bool"] = true,
                ["numeric_long"] = 1234567890123L,
                ["numeric_double"] = 3.14159,
                ["nested_dict"] = new Dictionary<string, object?> { ["inner_k"] = "inner_v" }
            }
        };

        var body = "## Заголовок заметки\n\nПараграф 1\n\n---\n\nГоризонтальный разделитель Markdown внутри тела!\n\n---\n\nКонец.";

        // Act: Serialize and Parse
        var serialized = FrontmatterCodec.Serialize(metadata, body, lineEnding: "\n");
        var parsed = FrontmatterCodec.Parse("notes/golden.md", serialized, FixedDate);

        // Assert: Invariants & Metadata
        Assert.Equal(metadata.Id, parsed.Metadata.Id);
        Assert.Equal(metadata.Title, parsed.Metadata.Title);
        Assert.Equal(metadata.Type, parsed.Metadata.Type);
        Assert.Equal(NoteStatus.OnHold, parsed.Metadata.Status);
        Assert.Equal(metadata.Tags, parsed.Metadata.Tags);
        Assert.Equal(metadata.Relations.Count, parsed.Metadata.Relations.Count);
        Assert.Equal(RelationKind.PartOf, parsed.Metadata.Relations[0].Kind);
        Assert.Equal(RelationKind.DuplicateOf, parsed.Metadata.Relations[1].Kind);
        Assert.Equal("yaml-stored-hash", parsed.Metadata.ContentHash);
        Assert.Equal(body, parsed.Body);

        // Unknown keys types and ordering
        Assert.Equal(true, parsed.Metadata.UnknownKeys["quoted_bool"]); //todo незнаю как правильно должен ли он парсится сракзу в bool??? с false такаяже тема...
        Assert.Equal(true, parsed.Metadata.UnknownKeys["actual_bool"]);
        Assert.Equal(1234567890123L, parsed.Metadata.UnknownKeys["numeric_long"]);
        Assert.Equal(3.14159, (double)parsed.Metadata.UnknownKeys["numeric_double"]!, 4);
    }

    [Fact]
    public void CanonicalKeysOrder_AndUnknownKeysAlphabetical_AreStrictlyMaintained()
    {
        var metadata = new NoteMetadata
        {
            Id = "id-1",
            Title = "Title",
            Type = NoteType.Diary,
            Usecase = "note",
            Status = NoteStatus.Active,
            Lang = "ru",
            Created = FixedDate,
            Updated = FixedDate,
            SchemaVersion = 1,
            UnknownKeys = new Dictionary<string, object?>
            {
                ["z_key"] = 1L,
                ["m_key"] = 2L,
                ["a_key"] = 3L
            }
        };

        var yaml = FrontmatterCodec.Serialize(metadata, "body", "\n");

        var lines = yaml.Split('\n');
        var aIdx = Array.FindIndex(lines, l => l.StartsWith("a_key:", StringComparison.Ordinal));
        var mIdx = Array.FindIndex(lines, l => l.StartsWith("m_key:", StringComparison.Ordinal));
        var zIdx = Array.FindIndex(lines, l => l.StartsWith("z_key:", StringComparison.Ordinal));
        var idIdx = Array.FindIndex(lines, l => l.StartsWith("id:", StringComparison.Ordinal));
        var schemaIdx = Array.FindIndex(lines, l => l.StartsWith("schema_version:", StringComparison.Ordinal));

        Assert.True(idIdx < schemaIdx);
        Assert.True(schemaIdx < aIdx);
        Assert.True(aIdx < mIdx);
        Assert.True(mIdx < zIdx);
    }

    [Fact]
    public void Invariant_Updated_IsNotMutatedDuringParsing()
    {
        var rawContent = "---\nid: test-updated\ntitle: Test\ntype: diary\ncreated: 2026-01-01T00:00:00.0000000Z\nupdated: 2026-01-01T12:00:00.0000000Z\n---\nbody";
        var fileModifiedTime = FixedDate;

        var doc = FrontmatterCodec.Parse("test.md", rawContent, fileModifiedTime);

        Assert.Equal(DateTimeOffset.Parse("2026-01-01T12:00:00.0000000Z", CultureInfo.InvariantCulture), doc.Metadata.Updated);
        Assert.NotEqual(fileModifiedTime, doc.Metadata.Updated);
        Assert.Equal(fileModifiedTime, doc.FileModifiedAt);
    }

    [Fact]
    public void Invariant_ContentHash_IsSha256OfUtf8Bytes()
    {
        var rawContent = "---\nid: test-hash\ntitle: Test\ntype: diary\ncontent_hash: old-yaml-hash\n---\nBody";
        var doc = FrontmatterCodec.Parse("test.md", rawContent, FixedDate);

        var expectedHex = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawContent)));

        Assert.Equal(expectedHex, doc.ContentHash);
        Assert.Equal("old-yaml-hash", doc.Metadata.ContentHash);
    }

    [Theory]
    [InlineData("\r\n", false)]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    [InlineData("\n", true)]
    public void Splitting_Handles_LineEndings_And_Bom(string lineEnding, bool withBom)
    {
        var text = $"---{lineEnding}id: bom-test{lineEnding}title: BOM{lineEnding}type: diary{lineEnding}---{lineEnding}Content line";
        if (withBom)
        {
            text = "\uFEFF" + text;
        }

        var ok = FrontmatterCodec.TrySplitFrontmatter(text, out var yaml, out var body, out var detectedEnding, out var error);

        Assert.True(ok, error);
        Assert.Equal(lineEnding, detectedEnding);
        Assert.Equal("Content line", body);
        Assert.Contains("id: bom-test", yaml);
    }

    [Fact]
    public void Delimiter_Dots_IsAcceptedAsClosingMarker()
    {
        var raw = "---\nid: dot-test\ntitle: Dots\ntype: diary\n...\nBody after dots";
        var doc = FrontmatterCodec.Parse("test.md", raw, FixedDate);

        Assert.Equal("dot-test", doc.Metadata.Id);
        Assert.Equal("Body after dots", doc.Body);
    }

    [Theory]
    [InlineData("")]                                                    // Пустой контент
    [InlineData("Без frontmatter")]                                     // Нет открывающего маркера
    [InlineData("id: 1\ntitle: test\n---\nbody")]                       // Нет '---' в первой строке
    [InlineData("---\nid: 1\ntitle: test")]                             // Нет закрывающего маркера
    [InlineData("---\n---\nbody")]                                      // Отсутствуют обязательные поля
    [InlineData("---\nid: '   '\ntitle: valid\ntype: diary\n---\n")]    // Пробельный ID
    [InlineData("---\nid: 1\ntitle: '   '\ntype: diary\n---\n")]        // Пробельный title
    [InlineData("---\nid: 1\ntitle: test\ntype: invalid_enum\n---\n")] // Некорректный type
    [InlineData("---\nid: 1\ntitle: test\ntype: diary\ncreated: bad-date\n---\n")] // Невалидная дата
    [InlineData("---\nid: 1\ntitle: test\ntype: diary\nconfidence: not-num\n---\n")] // Невалидное число
    [InlineData("---\nid: 1\ntitle: test\ntype: diary\nrelations:\n  - not_a_map\n---\n")] // relations не mapping
    public void TryParse_Fails_OnInvalidInputs(string raw)
    {
        var success = FrontmatterCodec.TryParse("bad.md", raw, FixedDate, out var doc, out var error);

        Assert.False(success);
        Assert.Null(doc);
        Assert.NotNull(error);
    }
}