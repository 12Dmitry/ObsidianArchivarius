using System.Security.Cryptography;
using System.Text;
using SecondBrain.Core.Eval;
using SecondBrain.Core.Model;
using YamlDotNet.Serialization;

namespace SecondBrain.Infrastructure.Eval;

public static class GoldDatasetLoader
{
    private static readonly DateTimeOffset BaseTimestamp = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();

    public static string ResolveGoldDirectory(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string fullExplicit = Path.GetFullPath(explicitPath);
            if (IsValidGoldDirectory(fullExplicit))
            {
                return fullExplicit;
            }

            throw new DirectoryNotFoundException(
                $"Explicit gold dataset directory '{fullExplicit}' does not exist or is missing corpus.yaml / links.yaml.");
        }

        string[] startDirectories =
        [
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        ];

        foreach (string start in startDirectories)
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                string candidateTestsGold = Path.Combine(current.FullName, "tests", "gold");
                if (IsValidGoldDirectory(candidateTestsGold))
                {
                    return candidateTestsGold;
                }

                string candidateGold = Path.Combine(current.FullName, "gold");
                if (IsValidGoldDirectory(candidateGold))
                {
                    return candidateGold;
                }

                if (IsValidGoldDirectory(current.FullName))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate 'tests/gold' directory containing corpus.yaml and links.yaml from '{Directory.GetCurrentDirectory()}' or '{AppContext.BaseDirectory}'.");
    }

    public static IReadOnlyList<NoteDocument> LoadCorpus(string corpusYamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusYamlPath);

        if (!File.Exists(corpusYamlPath))
        {
            throw new FileNotFoundException($"Corpus YAML file not found: '{corpusYamlPath}'.", corpusYamlPath);
        }

        string yaml = File.ReadAllText(corpusYamlPath, Encoding.UTF8);
        CorpusRootDto? root = YamlDeserializer.Deserialize<CorpusRootDto>(yaml);

        if (root?.Notes is null || root.Notes.Count == 0)
        {
            throw new InvalidDataException($"Corpus YAML '{corpusYamlPath}' contains no notes.");
        }

        var documents = new List<NoteDocument>(root.Notes.Count);

        for (int i = 0; i < root.Notes.Count; i++)
        {
            CorpusNoteDto dto = root.Notes[i]
                ?? throw new InvalidDataException($"Corpus note entry at index {i} is null.");

            string path = RequireNonEmpty(dto.Path, $"notes[{i}].path");
            string id = RequireNonEmpty(dto.Id, $"notes[{i}].id");
            string title = RequireNonEmpty(dto.Title, $"notes[{i}].title");
            string typeRaw = RequireNonEmpty(dto.Type, $"notes[{i}].type");
            string body = NormalizeLineEndings(RequireNonEmpty(dto.Body, $"notes[{i}].body")).Trim();

            if (!Enum.TryParse<NoteType>(typeRaw, ignoreCase: true, out NoteType noteType)
                || !Enum.IsDefined(noteType))
            {
                throw new InvalidDataException($"Unsupported note type '{typeRaw}' at notes[{i}].type.");
            }

            string contentHash = ComputeDeterministicHash(id, title, body);

            string[] tags = NormalizeStringList(dto.Tags, $"notes[{i}].tags");
            string[] up = NormalizeStringList(dto.Up, $"notes[{i}].up");
            string[] related = NormalizeStringList(dto.Related, $"notes[{i}].related");

            var metadata = new NoteMetadata
            {
                Id = id,
                Title = title,
                Type = noteType,
                Status = NoteStatus.Active,
                Tags = tags,
                Up = up,
                Related = related,
                Lang = "ru",
                Created = BaseTimestamp,
                Updated = BaseTimestamp,
                ContentHash = contentHash
            };

            documents.Add(new NoteDocument(
                Path: path,
                Metadata: metadata,
                Body: body,
                ContentHash: contentHash,
                FileModifiedAt: BaseTimestamp));
        }

        return documents;
    }

    public static IReadOnlyList<LinkEvalCase> LoadLinkCases(string linksYamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linksYamlPath);

        if (!File.Exists(linksYamlPath))
        {
            throw new FileNotFoundException($"Links YAML file not found: '{linksYamlPath}'.", linksYamlPath);
        }

        string yaml = File.ReadAllText(linksYamlPath, Encoding.UTF8);
        LinksRootDto? root = YamlDeserializer.Deserialize<LinksRootDto>(yaml);

        if (root?.Cases is null || root.Cases.Count == 0)
        {
            throw new InvalidDataException($"Links YAML '{linksYamlPath}' contains no cases.");
        }

        var cases = new List<LinkEvalCase>(root.Cases.Count);

        for (int i = 0; i < root.Cases.Count; i++)
        {
            LinkCaseDto dto = root.Cases[i]
                ?? throw new InvalidDataException($"Link eval case at index {i} is null.");

            string id = RequireNonEmpty(dto.Id, $"cases[{i}].id");
            string input = NormalizeLineEndings(RequireNonEmpty(dto.Input, $"cases[{i}].input")).Trim();
            string recipeId = RequireNonEmpty(dto.RecipeId, $"cases[{i}].recipe_id");

            LinkGoldDto goldDto = dto.Gold
                ?? throw new InvalidDataException($"cases[{i}].gold is missing.");

            string action = RequireNonEmpty(goldDto.Action, $"cases[{i}].gold.action");
            if (!string.Equals(action, "new", StringComparison.Ordinal) &&
                !string.Equals(action, "merge", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Invalid action '{action}' at cases[{i}].gold.action. Expected 'new' or 'merge'.");
            }

            string? duplicateOf = string.IsNullOrWhiteSpace(goldDto.DuplicateOf)
                ? null
                : goldDto.DuplicateOf.Trim();

            if (string.Equals(action, "merge", StringComparison.Ordinal) && duplicateOf is null)
            {
                throw new InvalidDataException(
                    $"Link eval case '{id}' has action 'merge' but 'duplicate_of' is null.");
            }

            if (string.Equals(action, "new", StringComparison.Ordinal) && duplicateOf is not null)
            {
                throw new InvalidDataException(
                    $"Link eval case '{id}' has action 'new' but 'duplicate_of' is '{duplicateOf}'.");
            }

            var mentions = new Dictionary<string, string>(StringComparer.Ordinal);
            if (goldDto.Mentions is not null)
            {
                foreach (KeyValuePair<string, string> pair in goldDto.Mentions)
                {
                    string mentionKey = RequireNonEmpty(pair.Key, $"cases[{i}].gold.mentions key");
                    string mentionTarget = RequireNonEmpty(pair.Value, $"cases[{i}].gold.mentions['{mentionKey}']");
                    if (!mentions.TryAdd(mentionKey, mentionTarget))
                    {
                        throw new InvalidDataException(
                            $"Duplicate mention key '{mentionKey}' at cases[{i}].gold.mentions.");
                    }
                }
            }

            var gold = new LinkGold(
                Related: NormalizeStringList(goldDto.Related, $"cases[{i}].gold.related"),
                Up: NormalizeStringList(goldDto.Up, $"cases[{i}].gold.up"),
                HardNegatives: NormalizeStringList(goldDto.HardNegatives, $"cases[{i}].gold.hard_negatives"),
                DuplicateOf: duplicateOf,
                Mentions: mentions,
                Action: action);

            cases.Add(new LinkEvalCase(
                Id: id,
                Input: input,
                RecipeId: recipeId,
                Gold: gold));
        }

        return cases;
    }

    private static bool IsValidGoldDirectory(string directoryPath) =>
        Directory.Exists(directoryPath) &&
        File.Exists(Path.Combine(directoryPath, "corpus.yaml")) &&
        File.Exists(Path.Combine(directoryPath, "links.yaml"));

    private static string RequireNonEmpty(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Required field '{fieldName}' is missing or empty.");
        }

        return value.Trim();
    }

    private static string[] NormalizeStringList(List<string>? values, string fieldName)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        var result = new string[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            result[i] = RequireNonEmpty(values[i], $"{fieldName}[{i}]");
        }

        return result;
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ComputeDeterministicHash(string id, string title, string body)
    {
        string payload = $"{id}\n{title}\n{body}";
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private sealed class CorpusRootDto
    {
        [YamlMember(Alias = "notes")]
        public List<CorpusNoteDto>? Notes { get; set; }
    }

    private sealed class CorpusNoteDto
    {
        [YamlMember(Alias = "path")]
        public string? Path { get; set; }

        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlMember(Alias = "title")]
        public string? Title { get; set; }

        [YamlMember(Alias = "type")]
        public string? Type { get; set; }

        [YamlMember(Alias = "tags")]
        public List<string>? Tags { get; set; }

        [YamlMember(Alias = "up")]
        public List<string>? Up { get; set; }

        [YamlMember(Alias = "related")]
        public List<string>? Related { get; set; }

        [YamlMember(Alias = "body")]
        public string? Body { get; set; }
    }

    private sealed class LinksRootDto
    {
        [YamlMember(Alias = "cases")]
        public List<LinkCaseDto>? Cases { get; set; }
    }

    private sealed class LinkCaseDto
    {
        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlMember(Alias = "input")]
        public string? Input { get; set; }

        [YamlMember(Alias = "recipe_id")]
        public string? RecipeId { get; set; }

        [YamlMember(Alias = "gold")]
        public LinkGoldDto? Gold { get; set; }
    }

    private sealed class LinkGoldDto
    {
        [YamlMember(Alias = "related")]
        public List<string>? Related { get; set; }

        [YamlMember(Alias = "up")]
        public List<string>? Up { get; set; }

        [YamlMember(Alias = "hard_negatives")]
        public List<string>? HardNegatives { get; set; }

        [YamlMember(Alias = "duplicate_of")]
        public string? DuplicateOf { get; set; }

        [YamlMember(Alias = "mentions")]
        public Dictionary<string, string>? Mentions { get; set; }

        [YamlMember(Alias = "action")]
        public string? Action { get; set; }
    }
}