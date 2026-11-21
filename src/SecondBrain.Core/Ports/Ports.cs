using SecondBrain.Core.Model;
using SecondBrain.Core.Recipes;

namespace SecondBrain.Core.Ports;

public interface IVaultStore
{
    Task<IReadOnlyList<VaultEntry>> ListAsync(string? prefix = null, CancellationToken ct = default);
    Task<NoteDocument?> ReadAsync(string path, CancellationToken ct = default);
    Task<WriteResult> WriteAsync(NoteDocument doc, WriteOptions options, CancellationToken ct = default);
    Task<bool> DeleteAsync(string path, string expectedHash, CancellationToken ct = default);
}

public interface IVaultIndex
{
    Task EnsureInitializedAsync(CancellationToken ct = default);
    Task UpsertAsync(NoteDocument doc, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default);
    Task<IndexStats> RebuildAsync(CancellationToken ct = default);
}

public sealed record SearchQuery(string Text, int Limit = 8, string[]? Tags = null, string? PathPrefix = null, NoteType? Type = null);

public sealed record SearchFilters(string[]? Tags = null, string? Type = null, string? Usecase = null, string? Status = null,
    string? PathPrefix = null, string[]? Up = null, bool Strict = false, int MinHits = 5);

public sealed record RelaxationStep(int Order, string Field, string Reason);

public sealed record SearchExplain(IReadOnlyList<RelaxationStep> Applied, double TagBoost, bool VectorUsed, int CandidatesBeforeFilters);

public interface IWriteJournal
{
    Task<JournalEntry> BeginAsync(WriteOperation op, CancellationToken ct = default);
    Task CommitAsync(Guid operationId, WriteResult result, CancellationToken ct = default);
    Task RollbackAsync(Guid operationId, CancellationToken ct = default);
}

public sealed record JournalEntry(Guid OperationId, string RunId, WriteOperation Operation, DateTimeOffset StartedAt);

public sealed record WritePlan(string RunId, IReadOnlyList<WriteOperation> Operations, IReadOnlyList<string> Warnings);

public sealed record WriteOperation(string Kind, string Path, string? ExpectedHash, string Payload, string Reason); // create|append_entry|replace_managed_block|patch_frontmatter

public interface IChatClient
{
    Task<T?> ExtractAsync<T>(string systemPrompt, string userPrompt, string jsonSchema, CancellationToken ct = default);
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}

public interface IChatRouter
{
    IChatClient Resolve(string profile);
    LlmProfile Describe(string profile);
}

public sealed record LlmProfile(string Provider, string Model, int NumCtx, double Temperature, double TopP, int TopK,
    double PresencePenalty, int MaxOutputTokens, int TimeoutSeconds);

public interface IEmbeddingClient
{
    string ModelId { get; }
    int Dimensions { get; }
    string QueryPrefix { get; }
    Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
    Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default);
}

public interface IEmbeddingQueue
{
    Task EnqueueAsync(IEnumerable<string> chunkIds, CancellationToken ct = default);
    Task<EmbedProgress> DrainAsync(int batchSize, int maxDegreeOfParallelism, CancellationToken ct = default);
}

public sealed record EmbedProgress(int Done, int Failed, int Remaining, double ChunksPerSecond);

public interface IMapGraph
{
    Task<IReadOnlyList<GraphEdge>> ExpandAsync(IReadOnlyList<string> paths, int hops = 1, int limit = 4, CancellationToken ct = default);
    Task<IReadOnlyList<MapCandidate>> ProposeMembershipAsync(string notePath, CancellationToken ct = default);
    Task<WritePlan> PlanMapSyncAsync(string mapPath, CancellationToken ct = default);
}

public sealed record GraphEdge(string Src, string Dst, string Kind, double Weight, string Origin);

public sealed record MapCandidate(string MapPath, string Title, double Score, string Reason);

public interface IPrivacyGate
{
    CloudPayload Prepare(string profile, IReadOnlyList<SearchHit> hits, string userQuery);
}

public sealed record CloudPayload(string SystemPrompt, string UserPrompt, int EstimatedTokens, string[] IncludedPaths, string[] RedactedPaths);

public interface IUseCaseHandler
{
    string RecipeId { get; }
    Task<CaptureOutcome> HandleAsync(CaptureContext ctx, CancellationToken ct = default);
}

public sealed record CaptureContext(string RawText, UseCaseRecipe Recipe, Directives Directives,
    IReadOnlyList<SearchHit> Candidates, string? SessionId, DateTimeOffset Now);

public sealed record Directives(string? Folder, string? NoteType, string[] Tags, string[] Maps, string[] Aliases, string? SourceUrl);

public abstract record CaptureOutcome
{
    public sealed record NeedInfo(string SessionId, IReadOnlyList<SlotQuestion> Questions) : CaptureOutcome;
    public sealed record Proposed(WritePlan Plan, string AnswerForUser) : CaptureOutcome;
    public sealed record Skipped(string Reason) : CaptureOutcome;
}

public sealed record SlotQuestion(string Slot, string Question, string? Hint);

public interface IElicitationSessionStore
{
    Task<ElicitationSession> StartAsync(string recipeId, string rawText, CancellationToken ct = default);
    Task<ElicitationSession?> LoadAsync(string sessionId, CancellationToken ct = default);
    Task SaveAsync(ElicitationSession session, CancellationToken ct = default);
    Task CompleteAsync(string sessionId, CancellationToken ct = default);
}

public sealed record ElicitationSession(string Id, string RecipeId, DateTimeOffset StartedAt,
    IReadOnlyDictionary<string, string> Slots, IReadOnlyList<string> RawFragments);