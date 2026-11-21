namespace SecondBrain.Core.Eval;

public sealed record EvalCase(string Id, string Input, string RecipeId, GoldExpectation Gold);

public sealed record GoldExpectation(string Type, string[] Tags, string[] Mentions, string[] MustContainFacts, string[] RequiredSlots, bool ShouldSkip);

public sealed record LlmMetrics(double SchemaValidRate, double RetryRate, double TypeAccuracy, double TagPrecision, double TagRecall,
    double MentionF1, double FactCoverage, double SlotAccuracy, int ForbiddenFieldLeaks, double P50Ms, double P95Ms);

public sealed record EmbeddingMetrics(int Dimensions, bool Normalized, double DeterminismCosine, int TruncationErrors,
    double RecallAt10, double Mrr, double ChunksPerSecond, double QueryP95Ms);

public sealed record EvalCaseResult(string Id, bool Passed, IReadOnlyList<string> Failures, string RawJson, double ElapsedMs);

public sealed record EvalReport(DateTimeOffset At, string Model, string Profile, string PromptPack, string PromptHash,
    LlmMetrics Llm, EmbeddingMetrics Embeddings, IReadOnlyList<EvalCaseResult> Cases);

public interface IEvalRunner
{
    Task<EvalReport> RunAsync(IReadOnlyList<EvalCase> cases, bool live, CancellationToken ct = default);
    Task<string> CompareAsync(string reportA, string reportB, CancellationToken ct = default);
}

public sealed record LinkEvalCase(string Id, string Input, string RecipeId, LinkGold Gold);

public sealed record LinkGold(string[] Related, string[] Up, string[] HardNegatives, string? DuplicateOf,
    IReadOnlyDictionary<string, string> Mentions, string Action);

public sealed record LinkMetrics(double RecallAt10, double Mrr, double LinkPrecisionAt5, double LinkRecallAt10, double LinkF1,
    double HardNegativeInTop5, double UpMapAccuracy, double MentionAccuracy, int FalseMerges, int FalseSplits,
    int OutOfCandidateLinks, double StabilityJaccard, double ArbitrationRate, double P95Ms);

public sealed record AblationConfig(string Name, bool Lexical, bool Vector, bool Graph, bool Llm,
    string ChunkTextVariant, double CosineThreshold, double MarginDelta);

public sealed record AblationResult(AblationConfig Config, LinkMetrics Metrics, int PromptTokens, string PromptHash);

public interface ILinkEvalRunner
{
    Task<IReadOnlyList<AblationResult>> RunAsync(IReadOnlyList<LinkEvalCase> cases, IReadOnlyList<AblationConfig> configs, bool live, CancellationToken ct = default);
    Task<string> CompareAsync(string reportA, string reportB, CancellationToken ct = default);
}