using System;
using System.Collections.Generic;
using System.Linq;

namespace SecondBrain.Infrastructure.Indexing;

public sealed record RankedCandidate(string Id, double Score);

public sealed record RrfHit(string Id, double Score, int? LexicalRank, int? VectorRank);

/// <summary>
/// Deterministic Reciprocal Rank Fusion (RRF) merger for lexical and vector search results.
/// </summary>
public static class RrfMerger
{
    public static IReadOnlyList<RrfHit> Merge(
        IReadOnlyList<RankedCandidate> lexical,
        IReadOnlyList<RankedCandidate> vector,
        int k = 60,
        double lexicalWeight = 1.0,
        double vectorWeight = 1.0,
        int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(k, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        if (limit == 0 || (lexical.Count == 0 && vector.Count == 0))
        {
            return Array.Empty<RrfHit>();
        }

        var lexicalRanks = ExtractBestRanks(lexical);
        var vectorRanks = ExtractBestRanks(vector);

        var allIds = new HashSet<string>(lexicalRanks.Count + vectorRanks.Count, StringComparer.Ordinal);
        foreach (string id in lexicalRanks.Keys)
        {
            allIds.Add(id);
        }

        foreach (string id in vectorRanks.Keys)
        {
            allIds.Add(id);
        }

        var hits = new List<RrfHit>(allIds.Count);
        foreach (string id in allIds)
        {
            int? lexRank = lexicalRanks.TryGetValue(id, out int lr) ? lr : null;
            int? vecRank = vectorRanks.TryGetValue(id, out int vr) ? vr : null;

            double score =
                (lexRank.HasValue ? lexicalWeight / ((double)k + lexRank.Value) : 0.0) +
                (vecRank.HasValue ? vectorWeight / ((double)k + vecRank.Value) : 0.0);

            hits.Add(new RrfHit(id, score, lexRank, vecRank));
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => Math.Min(hit.LexicalRank ?? int.MaxValue, hit.VectorRank ?? int.MaxValue))
            .ThenBy(hit => hit.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private static Dictionary<string, int> ExtractBestRanks(IReadOnlyList<RankedCandidate> candidates)
    {
        var ranks = new Dictionary<string, int>(candidates.Count, StringComparer.Ordinal);
        for (int i = 0; i < candidates.Count; i++)
        {
            ranks.TryAdd(candidates[i].Id, i + 1);
        }

        return ranks;
    }
}