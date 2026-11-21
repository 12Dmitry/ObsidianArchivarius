using System;
using System.Collections.Generic;
using System.Linq;
using SecondBrain.Core.Eval;

namespace SecondBrain.Infrastructure.Eval;

public sealed record LinkCaseEvaluation(
    string CaseId,
    LinkGold Gold,
    IReadOnlyList<string> RetrievedTop10,
    IReadOnlyList<string> ProposedLinks,
    IReadOnlyList<string> ProposedUpMaps,
    IReadOnlyDictionary<string, string> ResolvedMentions,
    string PredictedAction,
    string? PredictedDuplicateTarget,
    int OutOfCandidateCount,
    IReadOnlyList<string>? SecondRunProposedLinks,
    bool Arbitrated,
    double ElapsedMs);

public enum DedupDecisionKind
{
    New,
    Merge,
    Arbitrate
}

public sealed record DedupCandidate(string Path, double CosineScore);

public sealed record DedupDecision(
    DedupDecisionKind Kind,
    string? TargetPath,
    double TopScore,
    double Margin);

public sealed record DedupSweepCase(
    string CaseId,
    string? GoldDuplicateOf,
    IReadOnlyList<DedupCandidate> SortedCandidatesDescending);

public sealed record ThresholdSweepPoint(
    double Threshold,
    int FalseMerges,
    int FalseSplits,
    double F1);

/// <summary>
/// Deterministic evaluation metrics calculator and deduplication threshold decision engine.
/// </summary>
public static class LinkMetricsCalculator
{
    public static LinkMetrics Compute(IReadOnlyList<LinkCaseEvaluation> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);

        if (cases.Count == 0)
        {
            return new LinkMetrics(
                RecallAt10: 0.0,
                Mrr: 0.0,
                LinkPrecisionAt5: 0.0,
                LinkRecallAt10: 0.0,
                LinkF1: 0.0,
                HardNegativeInTop5: 0.0,
                UpMapAccuracy: 0.0,
                MentionAccuracy: 0.0,
                FalseMerges: 0,
                FalseSplits: 0,
                OutOfCandidateLinks: 0,
                StabilityJaccard: 0.0,
                ArbitrationRate: 0.0,
                P95Ms: 0.0);
        }

        double retrievalRecallSum = 0.0;
        double mrrSum = 0.0;
        int retrievalCaseCount = 0;

        double linkPrecisionSum = 0.0;
        int linkPrecisionCaseCount = 0;

        double linkRecallSum = 0.0;
        int linkRecallCaseCount = 0;

        int hardNegCases = 0;
        int hardNegHits = 0;

        int upMapCases = 0;
        int upMapHits = 0;

        int mentionTotal = 0;
        int mentionMatched = 0;

        int falseMerges = 0;
        int falseSplits = 0;
        int outOfCandidateLinks = 0;

        double stabilitySum = 0.0;
        int arbitratedCount = 0;
        var latencies = new double[cases.Count];

        for (int i = 0; i < cases.Count; i++)
        {
            LinkCaseEvaluation evaluation = cases[i];
            LinkGold gold = evaluation.Gold;

            var expectedLinks = new HashSet<string>(gold.Related, StringComparer.Ordinal);
            foreach (string upPath in gold.Up)
            {
                expectedLinks.Add(upPath);
            }

            var expectedRetrieval = new HashSet<string>(expectedLinks, StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(gold.DuplicateOf))
            {
                expectedRetrieval.Add(gold.DuplicateOf);
            }

            if (expectedRetrieval.Count > 0)
            {
                retrievalCaseCount++;

                var retrievedTop10Set = new HashSet<string>(
                    evaluation.RetrievedTop10.Take(10),
                    StringComparer.Ordinal);

                int retrievedHits = 0;
                foreach (string expected in expectedRetrieval)
                {
                    if (retrievedTop10Set.Contains(expected))
                    {
                        retrievedHits++;
                    }
                }

                retrievalRecallSum += retrievedHits / (double)expectedRetrieval.Count;

                double reciprocalRank = 0.0;
                for (int r = 0; r < evaluation.RetrievedTop10.Count; r++)
                {
                    if (expectedRetrieval.Contains(evaluation.RetrievedTop10[r]))
                    {
                        reciprocalRank = 1.0 / (r + 1);
                        break;
                    }
                }

                mrrSum += reciprocalRank;
            }

            List<string> proposedTop5 = evaluation.ProposedLinks.Take(5).ToList();
            if (expectedLinks.Count > 0 || proposedTop5.Count > 0)
            {
                linkPrecisionCaseCount++;
                if (proposedTop5.Count > 0)
                {
                    var proposedTop5Unique = new HashSet<string>(proposedTop5, StringComparer.Ordinal);
                    int hitsTop5 = proposedTop5Unique.Count(expectedLinks.Contains);
                    linkPrecisionSum += hitsTop5 / (double)proposedTop5.Count;
                }
            }

            if (expectedLinks.Count > 0)
            {
                linkRecallCaseCount++;
                var proposedTop10Unique = new HashSet<string>(
                    evaluation.ProposedLinks.Take(10),
                    StringComparer.Ordinal);
                int hitsTop10 = proposedTop10Unique.Count(expectedLinks.Contains);
                linkRecallSum += hitsTop10 / (double)expectedLinks.Count;
            }

            if (gold.HardNegatives.Length > 0)
            {
                hardNegCases++;
                var retrievedTop5 = new HashSet<string>(
                    evaluation.RetrievedTop10.Take(5),
                    StringComparer.Ordinal);
                if (gold.HardNegatives.Any(retrievedTop5.Contains))
                {
                    hardNegHits++;
                }
            }

            if (gold.Up.Length > 0)
            {
                upMapCases++;
                var proposedUpSet = new HashSet<string>(evaluation.ProposedUpMaps, StringComparer.Ordinal);
                if (gold.Up.All(proposedUpSet.Contains))
                {
                    upMapHits++;
                }
            }

            foreach (KeyValuePair<string, string> mention in gold.Mentions)
            {
                mentionTotal++;
                if (IsMentionMatched(evaluation.ResolvedMentions, mention.Key, mention.Value))
                {
                    mentionMatched++;
                }
            }

            bool isPredictedMerge = string.Equals(evaluation.PredictedAction, "merge", StringComparison.OrdinalIgnoreCase);
            bool isPredictedNew = string.Equals(evaluation.PredictedAction, "new", StringComparison.OrdinalIgnoreCase);

            if (isPredictedMerge &&
                (string.IsNullOrEmpty(gold.DuplicateOf) ||
                 !string.Equals(evaluation.PredictedDuplicateTarget, gold.DuplicateOf, StringComparison.Ordinal)))
            {
                falseMerges++;
            }

            if (!string.IsNullOrEmpty(gold.DuplicateOf) && isPredictedNew)
            {
                falseSplits++;
            }

            outOfCandidateLinks += evaluation.OutOfCandidateCount;
            stabilitySum += ComputeJaccard(evaluation.ProposedLinks, evaluation.SecondRunProposedLinks);

            if (evaluation.Arbitrated)
            {
                arbitratedCount++;
            }

            latencies[i] = evaluation.ElapsedMs;
        }

        double recallAt10 = retrievalCaseCount == 0 ? 0.0 : retrievalRecallSum / retrievalCaseCount;
        double mrr = retrievalCaseCount == 0 ? 0.0 : mrrSum / retrievalCaseCount;
        double linkPrecisionAt5 = linkPrecisionCaseCount == 0 ? 0.0 : linkPrecisionSum / linkPrecisionCaseCount;
        double linkRecallAt10 = linkRecallCaseCount == 0 ? 0.0 : linkRecallSum / linkRecallCaseCount;
        double linkF1 = (linkPrecisionAt5 + linkRecallAt10) == 0.0
            ? 0.0
            : (2.0 * linkPrecisionAt5 * linkRecallAt10) / (linkPrecisionAt5 + linkRecallAt10);
        double hardNegativeInTop5 = hardNegCases == 0 ? 0.0 : hardNegHits / (double)hardNegCases;
        double upMapAccuracy = upMapCases == 0 ? 0.0 : upMapHits / (double)upMapCases;
        double mentionAccuracy = mentionTotal == 0 ? 0.0 : mentionMatched / (double)mentionTotal;
        double stabilityJaccard = stabilitySum / cases.Count;
        double arbitrationRate = arbitratedCount / (double)cases.Count;
        double p95Ms = ComputeNearestRankPercentile(latencies, 0.95);

        return new LinkMetrics(
            RecallAt10: recallAt10,
            Mrr: mrr,
            LinkPrecisionAt5: linkPrecisionAt5,
            LinkRecallAt10: linkRecallAt10,
            LinkF1: linkF1,
            HardNegativeInTop5: hardNegativeInTop5,
            UpMapAccuracy: upMapAccuracy,
            MentionAccuracy: mentionAccuracy,
            FalseMerges: falseMerges,
            FalseSplits: falseSplits,
            OutOfCandidateLinks: outOfCandidateLinks,
            StabilityJaccard: stabilityJaccard,
            ArbitrationRate: arbitrationRate,
            P95Ms: p95Ms);
    }

    public static DedupDecision DecideDedup(
        IReadOnlyList<DedupCandidate> sortedDescending,
        double cosineThreshold = 0.92,
        double marginDelta = 0.03)
    {
        ArgumentNullException.ThrowIfNull(sortedDescending);

        if (sortedDescending.Count == 0)
        {
            return new DedupDecision(DedupDecisionKind.New, null, 0.0, 0.0);
        }

        DedupCandidate top1 = sortedDescending[0];
        double margin = sortedDescending.Count == 1
            ? top1.CosineScore
            : top1.CosineScore - sortedDescending[1].CosineScore;

        if (top1.CosineScore < cosineThreshold)
        {
            return new DedupDecision(DedupDecisionKind.New, null, top1.CosineScore, margin);
        }

        if (sortedDescending.Count == 1 || margin >= marginDelta)
        {
            return new DedupDecision(DedupDecisionKind.Merge, top1.Path, top1.CosineScore, margin);
        }

        return new DedupDecision(DedupDecisionKind.Arbitrate, null, top1.CosineScore, margin);
    }

    public static IReadOnlyList<ThresholdSweepPoint> SweepDedupThresholds(
        IReadOnlyList<DedupSweepCase> cases,
        double minThreshold = 0.86,
        double maxThreshold = 0.96,
        double step = 0.02,
        double marginDelta = 0.03)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, 0.0);
        if (maxThreshold < minThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxThreshold),
                maxThreshold,
                "maxThreshold must be greater than or equal to minThreshold.");
        }

        int steps = (int)Math.Floor(((maxThreshold - minThreshold) / step) + 1e-9);
        var results = new List<ThresholdSweepPoint>(steps + 1);

        for (int s = 0; s <= steps; s++)
        {
            double threshold = Math.Round(minThreshold + (s * step), 6, MidpointRounding.AwayFromZero);

            int falseMerges = 0;
            int falseSplits = 0;
            int truePositives = 0;
            int predictedMerges = 0;
            int actualDuplicates = 0;

            for (int i = 0; i < cases.Count; i++)
            {
                DedupSweepCase sweepCase = cases[i];
                bool hasGoldDuplicate = !string.IsNullOrEmpty(sweepCase.GoldDuplicateOf);
                if (hasGoldDuplicate)
                {
                    actualDuplicates++;
                }

                DedupDecision decision = DecideDedup(
                    sweepCase.SortedCandidatesDescending,
                    threshold,
                    marginDelta);

                if (decision.Kind == DedupDecisionKind.Merge)
                {
                    predictedMerges++;
                    if (hasGoldDuplicate &&
                        string.Equals(decision.TargetPath, sweepCase.GoldDuplicateOf, StringComparison.Ordinal))
                    {
                        truePositives++;
                    }
                    else
                    {
                        falseMerges++;
                    }
                }
                else if (decision.Kind == DedupDecisionKind.New && hasGoldDuplicate)
                {
                    falseSplits++;
                }
            }

            double precision = predictedMerges == 0 ? 0.0 : truePositives / (double)predictedMerges;
            double recall = actualDuplicates == 0 ? 0.0 : truePositives / (double)actualDuplicates;
            double f1 = (precision + recall) == 0.0
                ? 0.0
                : (2.0 * precision * recall) / (precision + recall);

            results.Add(new ThresholdSweepPoint(threshold, falseMerges, falseSplits, f1));
        }

        return results;
    }

    private static bool IsMentionMatched(
        IReadOnlyDictionary<string, string> resolvedMentions,
        string expectedLabel,
        string expectedPath)
    {
        if (resolvedMentions.TryGetValue(expectedLabel, out string? exactPath) &&
            string.Equals(exactPath, expectedPath, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (KeyValuePair<string, string> kvp in resolvedMentions)
        {
            if (string.Equals(kvp.Key, expectedLabel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(kvp.Value, expectedPath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static double ComputeJaccard(
        IReadOnlyList<string> firstRun,
        IReadOnlyList<string>? secondRun)
    {
        if (secondRun is null)
        {
            return 1.0;
        }

        var setA = new HashSet<string>(firstRun, StringComparer.Ordinal);
        var setB = new HashSet<string>(secondRun, StringComparer.Ordinal);

        if (setA.Count == 0 && setB.Count == 0)
        {
            return 1.0;
        }

        int intersection = 0;
        foreach (string item in setA)
        {
            if (setB.Contains(item))
            {
                intersection++;
            }
        }

        int union = setA.Count + setB.Count - intersection;
        return intersection / (double)union;
    }

    private static double ComputeNearestRankPercentile(double[] values, double quantile)
    {
        if (values.Length == 0)
        {
            return 0.0;
        }

        Array.Sort(values);
        int index = Math.Clamp((int)Math.Ceiling(quantile * values.Length) - 1, 0, values.Length - 1);
        return values[index];
    }
}