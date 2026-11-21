using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SecondBrain.Core.Tags;

public sealed record TagPolicy
{
    public string[] AllowedPrefixes { get; init; } = ["area", "project", "topic", "method", "person", "status"];
    public int MaxApplied { get; init; } = 5;
    public int ShortlistSize { get; init; } = 15;
    public bool UnknownToProposals { get; init; } = true;
}

public sealed record TagEntry(string Canonical, IReadOnlyList<string> Aliases, int UsageCount, string? Parent);

public sealed record TagResolution(string Label, string? Canonical, bool IsKnown, double Score, string Reason);

public interface ITagResolver
{
    Task<IReadOnlyList<TagEntry>> ShortlistAsync(string text, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<TagResolution>> ResolveAsync(IEnumerable<string> labels, CancellationToken ct = default);
    Task ProposeAsync(IReadOnlyList<string> unknownLabels, string notePath, CancellationToken ct = default);
}