using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SecondBrain.Core.Linking;

public enum RelationKind { Related, Supports, Contradicts, PartOf, DuplicateOf, Up }

public sealed record RelationProposal   // LLM-facing: labels only, no paths/ids/wikilinks
{
    [JsonPropertyName("target_label")] public required string TargetLabel { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "related";
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    [JsonPropertyName("confidence")] public double Confidence { get; init; }
}

public sealed record ResolvedRelation(string SrcPath, string DstPath, RelationKind Kind, double Weight, string Origin, string Reason);

public sealed record LinkDecision(IReadOnlyList<ResolvedRelation> Accepted, IReadOnlyList<string> RejectedOutOfCandidates, IReadOnlyList<string> RejectedByThreshold);