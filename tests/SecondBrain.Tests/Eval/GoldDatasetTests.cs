using System.Text;
using System.Text.RegularExpressions;
using SecondBrain.Core.Eval;
using SecondBrain.Core.Model;
using SecondBrain.Infrastructure.Eval;
using Xunit;
using YamlDotNet.Core;

namespace SecondBrain.Tests.Eval;

public sealed class GoldDatasetTests
{
    private static readonly Regex UlidRegex = new(
        "^[0-7][0-9A-HJKMNP-TV-Z]{25}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Corpus_IsDeterministic_Unique_AndInternallyConsistent()
    {
        string goldDir = GoldDatasetLoader.ResolveGoldDirectory();
        string corpusPath = Path.Combine(goldDir, "corpus.yaml");

        IReadOnlyList<NoteDocument> corpusFirst = GoldDatasetLoader.LoadCorpus(corpusPath);
        IReadOnlyList<NoteDocument> corpusSecond = GoldDatasetLoader.LoadCorpus(corpusPath);

        Assert.True(corpusFirst.Count >= 26, $"Expected at least 26 corpus notes, got {corpusFirst.Count}.");
        Assert.Equal(corpusFirst.Count, corpusSecond.Count);

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var notesByPath = new Dictionary<string, NoteDocument>(StringComparer.Ordinal);

        for (int i = 0; i < corpusFirst.Count; i++)
        {
            NoteDocument note = corpusFirst[i];
            NoteDocument repeat = corpusSecond[i];

            Assert.False(string.IsNullOrWhiteSpace(note.Path), $"Note at index {i} has empty Path.");
            Assert.True(UlidRegex.IsMatch(note.Metadata.Id), $"Note '{note.Path}' has non-ULID Id '{note.Metadata.Id}'.");
            Assert.False(string.IsNullOrWhiteSpace(note.Metadata.Title), $"Note '{note.Path}' has empty Title.");
            Assert.False(string.IsNullOrWhiteSpace(note.Body), $"Note '{note.Path}' has empty Body.");
            Assert.False(string.IsNullOrWhiteSpace(note.ContentHash), $"Note '{note.Path}' has empty ContentHash.");
            Assert.True(note.Metadata.Tags.Count > 0, $"Note '{note.Path}' must contain at least one tag.");
            Assert.Contains("## ", note.Body, StringComparison.Ordinal);
            Assert.Equal(NoteStatus.Active, note.Metadata.Status);
            Assert.Equal("ru", note.Metadata.Lang);

            Assert.True(paths.Add(note.Path), $"Duplicate corpus Path detected: '{note.Path}'.");
            Assert.True(ids.Add(note.Metadata.Id), $"Duplicate corpus Id detected: '{note.Metadata.Id}'.");

            Assert.Equal(note.ContentHash, repeat.ContentHash);
            Assert.Equal(note.Metadata.Created, repeat.Metadata.Created);
            Assert.Equal(note.Metadata.Updated, repeat.Metadata.Updated);
            Assert.Equal(note.FileModifiedAt, repeat.FileModifiedAt);

            notesByPath[note.Path] = note;
        }

        int mapCount = corpusFirst.Count(n => n.Metadata.Type == NoteType.Map);
        Assert.True(mapCount >= 3, $"Expected at least 3 map notes, got {mapCount}.");

        foreach (NoteDocument note in corpusFirst)
        {
            foreach (string upPath in note.Metadata.Up)
            {
                Assert.True(notesByPath.TryGetValue(upPath, out NoteDocument? mapDoc),
                    $"Corpus note '{note.Path}' references missing up path '{upPath}'.");
                Assert.NotNull(mapDoc);
                Assert.Equal(NoteType.Map, mapDoc.Metadata.Type);
                Assert.NotEqual(note.Path, upPath);
            }

            foreach (string relatedPath in note.Metadata.Related)
            {
                Assert.True(paths.Contains(relatedPath),
                    $"Corpus note '{note.Path}' references missing related path '{relatedPath}'.");
                Assert.NotEqual(note.Path, relatedPath);
            }
        }
    }

    [Fact]
    public void LinkCases_MeetCoverageRequirements_AndPassReferentialIntegrity()
    {
        string goldDir = GoldDatasetLoader.ResolveGoldDirectory();
        IReadOnlyList<NoteDocument> corpus = GoldDatasetLoader.LoadCorpus(Path.Combine(goldDir, "corpus.yaml"));
        IReadOnlyList<LinkEvalCase> cases = GoldDatasetLoader.LoadLinkCases(Path.Combine(goldDir, "links.yaml"));

        Assert.True(cases.Count >= 20, $"Expected at least 20 link eval cases, got {cases.Count}.");

        var corpusNotesByPath = corpus.ToDictionary(n => n.Path, StringComparer.Ordinal);
        var caseIds = new HashSet<string>(StringComparer.Ordinal);

        int withHardNegatives = 0;
        int withDuplicates = 0;
        int withMentions = 0;
        int withUp = 0;

        foreach (LinkEvalCase item in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id), "Link case has empty Id.");
            Assert.True(caseIds.Add(item.Id), $"Duplicate LinkEvalCase Id detected: '{item.Id}'.");
            Assert.False(string.IsNullOrWhiteSpace(item.Input), $"Case '{item.Id}' has empty Input.");
            Assert.Equal("insight", item.RecipeId);

            LinkGold gold = item.Gold;

            if (gold.HardNegatives.Length > 0)
            {
                withHardNegatives++;
            }

            if (gold.DuplicateOf is not null)
            {
                withDuplicates++;
                Assert.Equal("merge", gold.Action);
                Assert.True(corpusNotesByPath.ContainsKey(gold.DuplicateOf),
                    $"Case '{item.Id}' has unknown DuplicateOf path '{gold.DuplicateOf}'.");
            }
            else
            {
                Assert.Equal("new", gold.Action);
            }

            if (gold.Mentions.Count > 0)
            {
                withMentions++;
            }

            if (gold.Up.Length > 0)
            {
                withUp++;
            }

            var relatedSet = new HashSet<string>(gold.Related, StringComparer.Ordinal);

            if (gold.DuplicateOf is not null)
            {
                Assert.DoesNotContain(gold.DuplicateOf, relatedSet);
            }

            foreach (string relatedPath in gold.Related)
            {
                Assert.True(corpusNotesByPath.ContainsKey(relatedPath),
                    $"Case '{item.Id}' references missing Related path '{relatedPath}'.");
            }

            foreach (string upPath in gold.Up)
            {
                Assert.True(corpusNotesByPath.TryGetValue(upPath, out NoteDocument? upDoc),
                    $"Case '{item.Id}' references missing Up path '{upPath}'.");
                Assert.NotNull(upDoc);
                Assert.Equal(NoteType.Map, upDoc.Metadata.Type);
            }

            foreach (string hardNegativePath in gold.HardNegatives)
            {
                Assert.True(corpusNotesByPath.ContainsKey(hardNegativePath),
                    $"Case '{item.Id}' references missing HardNegatives path '{hardNegativePath}'.");
                Assert.False(relatedSet.Contains(hardNegativePath),
                    $"Case '{item.Id}' has '{hardNegativePath}' in both Related and HardNegatives.");
                Assert.NotEqual(gold.DuplicateOf, hardNegativePath);
            }

            foreach (KeyValuePair<string, string> mention in gold.Mentions)
            {
                Assert.Contains(mention.Key, item.Input, StringComparison.OrdinalIgnoreCase);
                Assert.True(corpusNotesByPath.ContainsKey(mention.Value),
                    $"Case '{item.Id}' mention '{mention.Key}' targets missing path '{mention.Value}'.");
            }
        }

        Assert.True(withHardNegatives >= 6, $"Expected >= 6 cases with HardNegatives, got {withHardNegatives}.");
        Assert.True(withDuplicates >= 4, $"Expected >= 4 duplicate/merge cases, got {withDuplicates}.");
        Assert.True(withMentions >= 5, $"Expected >= 5 cases with Mentions, got {withMentions}.");
        Assert.True(withUp >= 6, $"Expected >= 6 cases with Up maps, got {withUp}.");
    }

    [Fact]
    public void ResolveGoldDirectory_AcceptsValidExplicitPath_AndRejectsDirectoryMissingDatasetFiles()
    {
        string goldDir = GoldDatasetLoader.ResolveGoldDirectory();
        string resolved = GoldDatasetLoader.ResolveGoldDirectory(goldDir);
        Assert.Equal(Path.GetFullPath(goldDir), resolved);

        string emptyDir = Path.Combine(Path.GetTempPath(), $"sb-empty-gold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            Assert.Throws<DirectoryNotFoundException>(() => GoldDatasetLoader.ResolveGoldDirectory(emptyDir));
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Fact]
    public void LoadCorpus_ThrowsOnNumericNoteType_AndUnknownProperty()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"sb-corpus-err-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string numericTypePath = Path.Combine(tempDir, "numeric-type.yaml");
            WriteUtf8File(numericTypePath, """
                notes:
                  - path: 40_Insights/test.md
                    id: 01J900D0000000000000000099
                    title: Заголовок
                    type: "999"
                    tags:
                      - dev/test
                    up: []
                    related: []
                    body: |
                      ## Секция
                      Текст заметки.
                """);

            Assert.Throws<InvalidDataException>(() => GoldDatasetLoader.LoadCorpus(numericTypePath));

            string unknownPropPath = Path.Combine(tempDir, "unknown-prop.yaml");
            WriteUtf8File(unknownPropPath, """
                notes:
                  - path: 40_Insights/test.md
                    id: 01J900D0000000000000000099
                    title: Заголовок
                    type: insight
                    tags:
                      - dev/test
                    typo_field: unexpected
                    up: []
                    related: []
                    body: |
                      ## Секция
                      Текст заметки.
                """);

            Assert.Throws<YamlException>(() => GoldDatasetLoader.LoadCorpus(unknownPropPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadLinkCases_ThrowsOnInvalidAction_MergeMismatch_UnknownProperty_AndNullMention()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"sb-links-err-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string invalidActionPath = Path.Combine(tempDir, "invalid-action.yaml");
            WriteUtf8File(invalidActionPath, """
                cases:
                  - id: c1
                    input: Текст кейса
                    recipe_id: insight
                    gold:
                      related: []
                      up: []
                      hard_negatives: []
                      duplicate_of: null
                      mentions: {}
                      action: update
                """);
            Assert.Throws<InvalidDataException>(() => GoldDatasetLoader.LoadLinkCases(invalidActionPath));

            string mergeWithoutTargetPath = Path.Combine(tempDir, "merge-mismatch.yaml");
            WriteUtf8File(mergeWithoutTargetPath, """
                cases:
                  - id: c1
                    input: Текст кейса
                    recipe_id: insight
                    gold:
                      related: []
                      up: []
                      hard_negatives: []
                      duplicate_of: null
                      mentions: {}
                      action: merge
                """);
            Assert.Throws<InvalidDataException>(() => GoldDatasetLoader.LoadLinkCases(mergeWithoutTargetPath));

            string nullMentionPath = Path.Combine(tempDir, "null-mention.yaml");
            WriteUtf8File(nullMentionPath, """
                cases:
                  - id: c1
                    input: Иван Петров
                    recipe_id: insight
                    gold:
                      related: []
                      up: []
                      hard_negatives: []
                      duplicate_of: null
                      mentions:
                        Иван Петров: null
                      action: new
                """);
            Assert.Throws<InvalidDataException>(() => GoldDatasetLoader.LoadLinkCases(nullMentionPath));

            string unknownFieldPath = Path.Combine(tempDir, "unknown-field.yaml");
            WriteUtf8File(unknownFieldPath, """
                cases:
                  - id: c1
                    input: Текст кейса
                    recipe_id: insight
                    gold:
                      related: []
                      up: []
                      hard_negative: []
                      duplicate_of: null
                      mentions: {}
                      action: new
                """);
            Assert.Throws<YamlException>(() => GoldDatasetLoader.LoadLinkCases(unknownFieldPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteUtf8File(string path, string content)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        writer.Write(content);
    }
}