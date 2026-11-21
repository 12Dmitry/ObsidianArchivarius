using System.Collections.Generic;

namespace SecondBrain.Core.Recipes;

public sealed record UseCaseRecipe
{
    public required string Id { get; init; }
    public string DisplayName { get; init; } = "";
    public int Priority { get; init; } = 100;
    public IReadOnlyList<string> Triggers { get; init; } = [];
    public required string NoteType { get; init; }
    public required string Folder { get; init; }
    public LlmProfileRef Llm { get; init; } = new();
    public string PromptPack { get; init; } = "";
    public IReadOnlyList<SlotSpec> Slots { get; init; } = [];
    public LinkPolicy Links { get; init; } = new();
    public MapPolicy Maps { get; init; } = new();
    public DedupPolicy Dedup { get; init; } = new();
    public WritePolicy Write { get; init; } = new();
    public ChunkPolicy Chunks { get; init; } = new();
    public PrivacyPolicy Privacy { get; init; } = new();
}

public sealed record SlotSpec
{
    public required string Name { get; init; }
    public string Kind { get; init; } = "text";
    public bool Required { get; init; }
    public int MinItems { get; init; }
    public string Question { get; init; } = "";
    public int? DefaultOffsetDays { get; init; }
}

public sealed record LlmProfileRef { public string Profile { get; init; } = "extract"; public int MaxCalls { get; init; } = 1; public int NumCtx { get; init; } = 8192; }

public sealed record LinkPolicy(bool ResolveMentions = true, bool CreateStubs = false, int MaxRelated = 8);

public sealed record MapPolicy(bool ExplicitUp = true, string[]? TagRules = null, double VectorCandidates = 0.0, bool RequireConfirmation = true);

public sealed record DedupPolicy(bool Hash = true, bool NormTitle = true, double CosineThreshold = 0.92, bool Arbitrate = true);

public sealed record WritePolicy(bool DryRunDefault = true, string Mode = "create", string? PathTemplate = null, string? Anchor = null, string[]? ManagedBlocks = null, bool AllowBodyRewrite = false);

public sealed record ChunkPolicy(string Strategy = "by_header", int Size = 1100, int Overlap = 150);

public sealed record PrivacyPolicy(bool CloudAllowed = false, string[]? RedactPathPrefixes = null);