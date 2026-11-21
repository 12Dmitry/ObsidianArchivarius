using YamlDotNet.Serialization;
using SecondBrain.Core.Linking;

namespace SecondBrain.Core.Model;

public enum NoteType { Decision, Insight, Project, Task, Meeting, Person, Resource, Fleeting, Review, Diary, Map }

public enum NoteStatus { Active, Done, Archived, OnHold, Draft }

public sealed record NoteMetadata
{
    public const int CurrentSchemaVersion = 2;

    [YamlMember(Alias = "id")] public required string Id { get; init; }
    [YamlMember(Alias = "title")] public required string Title { get; init; }
    [YamlMember(Alias = "type")] public required NoteType Type { get; init; }
    [YamlMember(Alias = "usecase")] public string Usecase { get; init; } = "note";
    [YamlMember(Alias = "status")] public NoteStatus Status { get; init; } = NoteStatus.Active;
    [YamlMember(Alias = "tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    [YamlMember(Alias = "tags_proposed")] public IReadOnlyList<string> TagsProposed { get; init; } = [];
    [YamlMember(Alias = "aliases")] public IReadOnlyList<string> Aliases { get; init; } = [];
    [YamlMember(Alias = "related")] public IReadOnlyList<string> Related { get; init; } = [];
    [YamlMember(Alias = "up")] public IReadOnlyList<string> Up { get; init; } = [];
    [YamlMember(Alias = "relations")] public IReadOnlyList<ResolvedRelation> Relations { get; init; } = [];
    [YamlMember(Alias = "summary")] public string? Summary { get; init; }
    [YamlMember(Alias = "lang")] public string Lang { get; init; } = "ru";
    [YamlMember(Alias = "created")] public DateTimeOffset Created { get; init; }
    [YamlMember(Alias = "updated")] public DateTimeOffset Updated { get; init; }
    [YamlMember(Alias = "schema_version")] public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [YamlMember(Alias = "source")] public string? Source { get; init; }
    [YamlMember(Alias = "source_url")] public string? SourceUrl { get; init; }
    [YamlMember(Alias = "content_hash")] public string? ContentHash { get; init; }
    [YamlMember(Alias = "mentions_raw")] public IReadOnlyList<string> MentionsRaw { get; init; } = [];
    [YamlMember(Alias = "links_origin")] public IReadOnlyDictionary<string, string> LinksOrigin { get; init; } = new Dictionary<string, string>();
    [YamlMember(Alias = "llm")] public LlmProvenance? Llm { get; init; }
    [YamlMember(Alias = "privacy")] public PrivacyInfo Privacy { get; init; } = new();
    [YamlMember(Alias = "processed")] public bool? Processed { get; init; }
    [YamlMember(Alias = "generated_notes")] public IReadOnlyList<string> GeneratedNotes { get; init; } = [];
    [YamlMember(Alias = "stub")] public bool Stub { get; init; }
    [YamlMember(Alias = "archived_at")] public DateTimeOffset? ArchivedAt { get; init; }
    [YamlMember(Alias = "review_date")] public DateTimeOffset? ReviewDate { get; init; }
    [YamlMember(Alias = "reviewed_at")] public DateTimeOffset? ReviewedAt { get; init; }
    [YamlMember(Alias = "confidence")] public double? Confidence { get; init; }
    [YamlMember(Alias = "slots")] public IReadOnlyDictionary<string, object?> Slots { get; init; } = new Dictionary<string, object?>();
    [YamlMember(Alias = "decision_log")] public IReadOnlyDictionary<string, object?> DecisionLog { get; init; } = new Dictionary<string, object?>();
    [YamlMember(Alias = "project")] public IReadOnlyDictionary<string, object?> Project { get; init; } = new Dictionary<string, object?>();
    [YamlMember(Alias = "extra")] public IReadOnlyDictionary<string, object?> Extra { get; init; } = new Dictionary<string, object?>();
    [YamlIgnore] public IReadOnlyDictionary<string, object?> UnknownKeys { get; init; } = new Dictionary<string, object?>();
}

public sealed record LlmProvenance
{
    [YamlMember(Alias = "model")] public string Model { get; init; } = "";
    [YamlMember(Alias = "profile")] public string Profile { get; init; } = "";
    [YamlMember(Alias = "prompt_pack")] public string PromptPack { get; init; } = "";
    [YamlMember(Alias = "prompt_hash")] public string PromptHash { get; init; } = "";
    [YamlMember(Alias = "run_id")] public string RunId { get; init; } = "";
    [YamlMember(Alias = "extracted_at")] public DateTimeOffset ExtractedAt { get; init; }
}

public sealed record PrivacyInfo
{
    [YamlMember(Alias = "cloud_allowed")] public bool CloudAllowed { get; init; }
    [YamlMember(Alias = "sensitivity")] public string Sensitivity { get; init; } = "normal";
}

public sealed record NoteDocument(string Path, NoteMetadata Metadata, string Body, string ContentHash, DateTimeOffset FileModifiedAt);

public sealed record VaultEntry(string Path, NoteType? Type, string Title, DateTimeOffset ModifiedAt, long SizeBytes);

public sealed record WriteResult(bool Success, string? Hash, string? Diff, string? Error);

public sealed record WriteOptions(string? ExpectedHash = null, bool DryRun = false, string Reason = "");

public sealed record SearchHit(string Path, string Title, string Snippet, double Score, string? Header, string[] Tags);

public sealed record SearchTiming(double LexicalMs, double VectorMs, double TotalMs);

public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, SearchTiming Timing);

public sealed record IndexStats(int Chunks, int Vectors, DateTimeOffset BuiltAt, string EmbeddingModel, int Dimensions);