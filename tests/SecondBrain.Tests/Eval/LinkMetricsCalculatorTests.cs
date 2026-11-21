using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using SecondBrain.Core.Eval;
using SecondBrain.Infrastructure.Eval;
using Xunit;

namespace SecondBrain.Tests.Eval;

public sealed class LinkMetricsCalculatorTests
{
    [Fact]
    public void Compute_ControlMiniSet_Produces_Exact_Expected_Metrics()
    {
        var cases = new[]
        {
            new LinkCaseEvaluation(
                CaseId: "c1",
                Gold: new LinkGold(
                    Related: new[] { "a.md", "b.md" },
                    Up: new[] { "moc/root.md" },
                    HardNegatives: new[] { "hn1.md" },
                    DuplicateOf: null,
                    Mentions: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Alpha"] = "a.md",
                        ["Beta"] = "b.md"
                    },
                    Action: "new"),
                RetrievedTop10: new[] { "x1.md", "hn1.md", "a.md", "moc/root.md", "x2.md" },
                ProposedLinks: new[] { "a.md", "x1.md", "b.md", "x2.md", "x3.md", "moc/root.md" },
                ProposedUpMaps: new[] { "other.md" },
                ResolvedMentions: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["alpha"] = "a.md",
                    ["Beta"] = "wrong.md"
                },
                PredictedAction: "new",
                PredictedDuplicateTarget: null,
                OutOfCandidateCount: 1,
                SecondRunProposedLinks: new[] { "a.md", "x1.md", "b.md", "x2.md", "x3.md", "moc/root.md" },
                Arbitrated: false,
                ElapsedMs: 12.0),

            new LinkCaseEvaluation(
                CaseId: "c2",
                Gold: new LinkGold(
                    Related: new[] { "d.md", "e.md" },
                    Up: new[] { "moc/hub.md" },
                    HardNegatives: new[] { "hn2.md" },
                    DuplicateOf: null,
                    Mentions: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Delta"] = "d.md"
                    },
                    Action: "new"),
                RetrievedTop10: new[] { "d.md", "x1.md", "x2.md", "x3.md", "x4.md", "hn2.md" },
                ProposedLinks: new[] { "d.md", "moc/hub.md" },
                ProposedUpMaps: new[] { "moc/hub.md", "extra.md" },
                ResolvedMentions: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Delta"] = "d.md"
                },
                PredictedAction: "new",
                PredictedDuplicateTarget: null,
                OutOfCandidateCount: 0,
                SecondRunProposedLinks: new[] { "d.md", "e.md" },
                Arbitrated: true,
                ElapsedMs: 45.0),

            new LinkCaseEvaluation(
                CaseId: "c3",
                Gold: new LinkGold(
                    Related: Array.Empty<string>(),
                    Up: Array.Empty<string>(),
                    HardNegatives: Array.Empty<string>(),
                    DuplicateOf: "orig.md",
                    Mentions: new Dictionary<string, string>(),
                    Action: "merge"),
                RetrievedTop10: new[] { "wrong.md", "orig.md" },
                ProposedLinks: Array.Empty<string>(),
                ProposedUpMaps: Array.Empty<string>(),
                ResolvedMentions: new Dictionary<string, string>(),
                PredictedAction: "merge",
                PredictedDuplicateTarget: "wrong.md",
                OutOfCandidateCount: 2,
                SecondRunProposedLinks: Array.Empty<string>(),
                Arbitrated: false,
                ElapsedMs: 8.0),

            new LinkCaseEvaluation(
                CaseId: "c4",
                Gold: new LinkGold(
                    Related: Array.Empty<string>(),
                    Up: Array.Empty<string>(),
                    HardNegatives: Array.Empty<string>(),
                    DuplicateOf: "target.md",
                    Mentions: new Dictionary<string, string>(),
                    Action: "merge"),
                RetrievedTop10: new[] { "x1.md", "x2.md" },
                ProposedLinks: Array.Empty<string>(),
                ProposedUpMaps: Array.Empty<string>(),
                ResolvedMentions: new Dictionary<string, string>(),
                PredictedAction: "new",
                PredictedDuplicateTarget: null,
                OutOfCandidateCount: 0,
                SecondRunProposedLinks: null,
                Arbitrated: false,
                ElapsedMs: 20.0),

            new LinkCaseEvaluation(
                CaseId: "c5",
                Gold: new LinkGold(
                    Related: new[] { "r1.md" },
                    Up: Array.Empty<string>(),
                    HardNegatives: Array.Empty<string>(),
                    DuplicateOf: null,
                    Mentions: new Dictionary<string, string>(),
                    Action: "new"),
                RetrievedTop10: new[] { "r1.md" },
                ProposedLinks: new[] { "r1.md" },
                ProposedUpMaps: Array.Empty<string>(),
                ResolvedMentions: new Dictionary<string, string>(),
                PredictedAction: "merge",
                PredictedDuplicateTarget: "r1.md",
                OutOfCandidateCount: 1,
                SecondRunProposedLinks: new[] { "r1.md" },
                Arbitrated: true,
                ElapsedMs: 30.0)
        };

        LinkMetrics metrics = LinkMetricsCalculator.Compute(cases);

        metrics.RecallAt10.Should().BeApproximately(0.6, 1e-10);
        metrics.Mrr.Should().BeApproximately((1.0 / 3.0 + 1.0 + 0.5 + 0.0 + 1.0) / 5.0, 1e-10);
        metrics.LinkPrecisionAt5.Should().BeApproximately(0.8, 1e-10);
        metrics.LinkRecallAt10.Should().BeApproximately((1.0 + 2.0 / 3.0 + 1.0) / 3.0, 1e-10);
        metrics.LinkF1.Should().BeApproximately(2.0 * 0.8 * (8.0 / 9.0) / (0.8 + 8.0 / 9.0), 1e-10);
        metrics.HardNegativeInTop5.Should().BeApproximately(0.5, 1e-10);
        metrics.UpMapAccuracy.Should().BeApproximately(0.5, 1e-10);
        metrics.MentionAccuracy.Should().BeApproximately(2.0 / 3.0, 1e-10);
        metrics.FalseMerges.Should().Be(2);
        metrics.FalseSplits.Should().Be(1);
        metrics.OutOfCandidateLinks.Should().Be(4);
        metrics.StabilityJaccard.Should().BeApproximately((1.0 + 1.0 / 3.0 + 1.0 + 1.0 + 1.0) / 5.0, 1e-10);
        metrics.ArbitrationRate.Should().BeApproximately(0.4, 1e-10);
        metrics.P95Ms.Should().Be(45.0);
    }

    [Fact]
    public void Compute_EmptyCases_Returns_AllZeroMetrics()
    {
        LinkMetrics metrics = LinkMetricsCalculator.Compute(Array.Empty<LinkCaseEvaluation>());

        metrics.Should().Be(new LinkMetrics(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0, 0, 0, 0.0, 0.0, 0.0));
    }

    [Fact]
    public void Compute_P95Ms_Computes_95th_Percentile_Across_Twenty_Cases()
    {
        var cases = Enumerable.Range(1, 20)
            .Select(ms => new LinkCaseEvaluation(
                CaseId: $"p{ms}",
                Gold: new LinkGold(
                    Related: Array.Empty<string>(),
                    Up: Array.Empty<string>(),
                    HardNegatives: Array.Empty<string>(),
                    DuplicateOf: null,
                    Mentions: new Dictionary<string, string>(),
                    Action: "new"),
                RetrievedTop10: Array.Empty<string>(),
                ProposedLinks: Array.Empty<string>(),
                ProposedUpMaps: Array.Empty<string>(),
                ResolvedMentions: new Dictionary<string, string>(),
                PredictedAction: "new",
                PredictedDuplicateTarget: null,
                OutOfCandidateCount: 0,
                SecondRunProposedLinks: null,
                Arbitrated: false,
                ElapsedMs: 21 - ms))
            .ToArray();

        LinkMetrics metrics = LinkMetricsCalculator.Compute(cases);

        metrics.P95Ms.Should().Be(19.0);
    }

    [Fact]
    public void DecideDedup_Covers_New_Merge_And_Arbitrate_Transitions()
    {
        DedupDecision empty = LinkMetricsCalculator.DecideDedup(Array.Empty<DedupCandidate>());
        empty.Kind.Should().Be(DedupDecisionKind.New);
        empty.TargetPath.Should().BeNull();
        empty.TopScore.Should().Be(0.0);

        DedupDecision belowThreshold = LinkMetricsCalculator.DecideDedup(
            new[] { new DedupCandidate("a.md", 0.919) },
            cosineThreshold: 0.92,
            marginDelta: 0.03);
        belowThreshold.Kind.Should().Be(DedupDecisionKind.New);
        belowThreshold.TargetPath.Should().BeNull();
        belowThreshold.TopScore.Should().BeApproximately(0.919, 1e-12);

        DedupDecision singleAboveThreshold = LinkMetricsCalculator.DecideDedup(
            new[] { new DedupCandidate("a.md", 0.93) },
            cosineThreshold: 0.92,
            marginDelta: 0.03);
        singleAboveThreshold.Kind.Should().Be(DedupDecisionKind.Merge);
        singleAboveThreshold.TargetPath.Should().Be("a.md");

        DedupDecision clearMarginMerge = LinkMetricsCalculator.DecideDedup(
            new[]
            {
                new DedupCandidate("a.md", 0.95),
                new DedupCandidate("b.md", 0.91)
            },
            cosineThreshold: 0.92,
            marginDelta: 0.03);
        clearMarginMerge.Kind.Should().Be(DedupDecisionKind.Merge);
        clearMarginMerge.TargetPath.Should().Be("a.md");
        clearMarginMerge.Margin.Should().BeApproximately(0.04, 1e-12);

        DedupDecision denseClusterArbitrate = LinkMetricsCalculator.DecideDedup(
            new[]
            {
                new DedupCandidate("a.md", 0.94),
                new DedupCandidate("b.md", 0.92)
            },
            cosineThreshold: 0.92,
            marginDelta: 0.03);
        denseClusterArbitrate.Kind.Should().Be(DedupDecisionKind.Arbitrate);
        denseClusterArbitrate.TargetPath.Should().BeNull();
        denseClusterArbitrate.TopScore.Should().BeApproximately(0.94, 1e-12);
        denseClusterArbitrate.Margin.Should().BeApproximately(0.02, 1e-12);
    }

    [Fact]
    public void SweepDedupThresholds_Evaluates_Range_086_To_096_And_Identifies_Safe_ZeroFalseMerge_Threshold()
    {
        var sweepCases = new[]
        {
            new DedupSweepCase(
                "d1",
                "a.md",
                new[] { new DedupCandidate("a.md", 0.95), new DedupCandidate("z.md", 0.80) }),
            new DedupSweepCase(
                "d2",
                "b.md",
                new[] { new DedupCandidate("b.md", 0.91), new DedupCandidate("z.md", 0.80) }),
            new DedupSweepCase(
                "d3",
                null,
                new[] { new DedupCandidate("c.md", 0.89), new DedupCandidate("z.md", 0.70) }),
            new DedupSweepCase(
                "d4",
                null,
                new[] { new DedupCandidate("d.md", 0.85) })
        };

        IReadOnlyList<ThresholdSweepPoint> points = LinkMetricsCalculator.SweepDedupThresholds(
            sweepCases,
            minThreshold: 0.86,
            maxThreshold: 0.96,
            step: 0.02,
            marginDelta: 0.03);

        points.Should().HaveCount(6);
        points.Select(p => p.Threshold).Should().Equal(new[] { 0.86, 0.88, 0.90, 0.92, 0.94, 0.96 });

        points[0].Should().Be(new ThresholdSweepPoint(0.86, FalseMerges: 1, FalseSplits: 0, F1: 0.8));
        points[1].Should().Be(new ThresholdSweepPoint(0.88, FalseMerges: 1, FalseSplits: 0, F1: 0.8));
        points[2].Should().Be(new ThresholdSweepPoint(0.90, FalseMerges: 0, FalseSplits: 0, F1: 1.0));
        points[3].Threshold.Should().Be(0.92);
        points[3].FalseMerges.Should().Be(0);
        points[3].FalseSplits.Should().Be(1);
        points[3].F1.Should().BeApproximately(2.0 / 3.0, 1e-10);
        points[4].Threshold.Should().Be(0.94);
        points[4].FalseMerges.Should().Be(0);
        points[4].FalseSplits.Should().Be(1);
        points[4].F1.Should().BeApproximately(2.0 / 3.0, 1e-10);
        points[5].Should().Be(new ThresholdSweepPoint(0.96, FalseMerges: 0, FalseSplits: 2, F1: 0.0));

        ThresholdSweepPoint bestSafe = points
            .Where(p => p.FalseMerges == 0)
            .OrderByDescending(p => p.F1)
            .First();

        bestSafe.Threshold.Should().Be(0.90);
        bestSafe.F1.Should().Be(1.0);
    }

    [Fact]
    public void SweepDedupThresholds_Validates_Parameters()
    {
        var empty = Array.Empty<DedupSweepCase>();

        Action nonPositiveStep = () => LinkMetricsCalculator.SweepDedupThresholds(empty, step: 0.0);
        Action invalidRange = () => LinkMetricsCalculator.SweepDedupThresholds(empty, minThreshold: 0.95, maxThreshold: 0.90);

        nonPositiveStep.Should().Throw<ArgumentOutOfRangeException>();
        invalidRange.Should().Throw<ArgumentOutOfRangeException>();
    }
}