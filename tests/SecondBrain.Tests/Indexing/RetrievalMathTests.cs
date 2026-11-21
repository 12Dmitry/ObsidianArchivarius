using System;
using System.Numerics;
using FluentAssertions;
using SecondBrain.Infrastructure.Indexing;
using Xunit;

namespace SecondBrain.Tests.Indexing;

public sealed class RetrievalMathTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(1027)]
    [InlineData(1)]
    [InlineData(7)]
    public void Dot_And_CosineSimilarity_Match_Scalar_Reference_Across_Simd_And_Tail_Dimensions(int dimensions)
    {
        if (dimensions == 1027)
        {
            (dimensions % Vector<float>.Count).Should().NotBe(0);
        }

        float[] a = CreateDeterministicVector(dimensions, seedOffset: 0.17f);
        float[] b = CreateDeterministicVector(dimensions, seedOffset: 1.31f);

        double expectedDot = 0.0;
        double expectedNormA = 0.0;
        double expectedNormB = 0.0;
        for (int i = 0; i < dimensions; i++)
        {
            expectedDot += (double)a[i] * b[i];
            expectedNormA += (double)a[i] * a[i];
            expectedNormB += (double)b[i] * b[i];
        }

        float expectedCosine = (float)(expectedDot / (Math.Sqrt(expectedNormA) * Math.Sqrt(expectedNormB)));

        SimdCosine.Dot(a, b).Should().BeApproximately((float)expectedDot, 1e-3f);
        SimdCosine.CosineSimilarity(a, b).Should().BeApproximately(expectedCosine, 1e-5f);
    }

    [Fact]
    public void CosineSimilarity_Handles_Identical_Orthogonal_Opposite_And_ZeroNorm_Vectors()
    {
        float[] v = CreateDeterministicVector(1027, seedOffset: 0.5f);
        float[] opposite = new float[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            opposite[i] = -v[i];
        }

        float[] orthoA = new float[1027];
        float[] orthoB = new float[1027];
        orthoA[0] = 3.0f;
        orthoA[1026] = 4.0f;
        orthoB[0] = -4.0f;
        orthoB[1026] = 3.0f;

        float[] zero = new float[1027];

        SimdCosine.CosineSimilarity(v, v).Should().BeApproximately(1.0f, 1e-6f);
        SimdCosine.CosineSimilarity(v, opposite).Should().BeApproximately(-1.0f, 1e-6f);
        SimdCosine.CosineSimilarity(orthoA, orthoB).Should().BeApproximately(0.0f, 1e-6f);
        SimdCosine.CosineSimilarity(v, zero).Should().Be(0.0f);
        SimdCosine.CosineSimilarity(zero, zero).Should().Be(0.0f);
        SimdCosine.CosineSimilarity(Array.Empty<float>(), Array.Empty<float>()).Should().Be(0.0f);
    }

    [Fact]
    public void CosineNormalized_Matches_Normalized_Cosine_And_Clamps_Range()
    {
        float[] rawA = CreateDeterministicVector(1027, seedOffset: 0.25f);
        float[] rawB = CreateDeterministicVector(1027, seedOffset: 0.85f);

        float[] normA = SimdCosine.NormalizeL2(rawA);
        float[] normB = SimdCosine.NormalizeL2(rawB);

        float expected = SimdCosine.CosineSimilarity(rawA, rawB);
        SimdCosine.CosineNormalized(normA, normB).Should().BeApproximately(expected, 1e-5f);

        float[] slightlyOverUnit = new[] { 1.0005f, 0.0f };
        SimdCosine.CosineNormalized(slightlyOverUnit, new[] { 1.0f, 0.0f }).Should().Be(1.0f);
        SimdCosine.CosineNormalized(slightlyOverUnit, new[] { -1.0f, 0.0f }).Should().Be(-1.0f);
    }

    [Fact]
    public void NormalizeL2_Produces_Unit_Norm_And_Preserves_Direction()
    {
        float[] raw = CreateDeterministicVector(1027, seedOffset: 0.42f);
        float[] normalized = SimdCosine.NormalizeL2(raw);

        normalized.Length.Should().Be(1027);
        SimdCosine.Dot(normalized, normalized).Should().BeApproximately(1.0f, 1e-5f);
        SimdCosine.CosineSimilarity(raw, normalized).Should().BeApproximately(1.0f, 1e-6f);
    }

    [Fact]
    public void NormalizeL2_Throws_InvalidOperationException_On_ZeroNorm_Or_Empty()
    {
        float[] zero = new float[1027];
        float[] empty = Array.Empty<float>();

        Action zeroAct = () => SimdCosine.NormalizeL2(zero);
        Action emptyAct = () => SimdCosine.NormalizeL2(empty);

        zeroAct.Should().Throw<InvalidOperationException>();
        emptyAct.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void VectorOperations_Throw_ArgumentException_When_Lengths_Mismatch()
    {
        float[] a = new float[1024];
        float[] b = new float[1027];

        Action dotAct = () => SimdCosine.Dot(a, b);
        Action cosAct = () => SimdCosine.CosineSimilarity(a, b);
        Action normCosAct = () => SimdCosine.CosineNormalized(a, b);

        dotAct.Should().Throw<ArgumentException>();
        cosAct.Should().Throw<ArgumentException>();
        normCosAct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToBytes_And_FromBytes_RoundTrip_Preserves_Exact_Floats()
    {
        float[] original = CreateDeterministicVector(1027, seedOffset: 0.91f);

        byte[] bytes = SimdCosine.ToBytes(original);
        bytes.Length.Should().Be(1027 * sizeof(float));

        float[] restored = SimdCosine.FromBytes(bytes);
        restored.Should().Equal(original);

        byte[] emptyBytes = SimdCosine.ToBytes(Array.Empty<float>());
        emptyBytes.Should().BeEmpty();
        SimdCosine.FromBytes(emptyBytes).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void FromBytes_Throws_ArgumentException_When_Length_Not_Divisible_By_FloatSize(int byteLength)
    {
        byte[] invalidBytes = new byte[byteLength];

        Action act = () => SimdCosine.FromBytes(invalidBytes);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RrfMerger_Boosts_Overlapping_Candidates_Above_SingleList_Candidates()
    {
        var lexical = new[]
        {
            new RankedCandidate("lex-top", 10.0),
            new RankedCandidate("shared", 8.0),
            new RankedCandidate("lex-only", 5.0)
        };
        var vector = new[]
        {
            new RankedCandidate("vec-top", 0.99),
            new RankedCandidate("shared", 0.95),
            new RankedCandidate("vec-only", 0.80)
        };

        var hits = RrfMerger.Merge(lexical, vector, k: 60);

        hits.Should().HaveCount(5);
        hits[0].Id.Should().Be("shared");
        hits[0].LexicalRank.Should().Be(2);
        hits[0].VectorRank.Should().Be(2);
        hits[0].Score.Should().BeApproximately(2.0 / 62.0, 1e-12);
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    [Fact]
    public void RrfMerger_Ignores_Duplicates_Within_Same_List_And_Keeps_Best_Rank()
    {
        var lexical = new[]
        {
            new RankedCandidate("doc-a", 10.0),
            new RankedCandidate("doc-b", 9.0),
            new RankedCandidate("doc-a", 4.0),
            new RankedCandidate("doc-a", 1.0)
        };
        var vector = Array.Empty<RankedCandidate>();

        var hits = RrfMerger.Merge(lexical, vector, k: 60);

        hits.Should().HaveCount(2);
        hits[0].Should().Be(new RrfHit("doc-a", 1.0 / 61.0, 1, null));
        hits[1].Should().Be(new RrfHit("doc-b", 1.0 / 62.0, 2, null));
    }

    [Fact]
    public void RrfMerger_Handles_Empty_Lists_And_Limit()
    {
        RrfMerger.Merge(Array.Empty<RankedCandidate>(), Array.Empty<RankedCandidate>())
            .Should().BeEmpty();

        var lexical = new[]
        {
            new RankedCandidate("a", 3.0),
            new RankedCandidate("b", 2.0),
            new RankedCandidate("c", 1.0)
        };

        RrfMerger.Merge(lexical, Array.Empty<RankedCandidate>(), limit: 0).Should().BeEmpty();

        var limited = RrfMerger.Merge(lexical, Array.Empty<RankedCandidate>(), limit: 2);
        limited.Select(x => x.Id).Should().Equal(new[] { "a", "b" });
    }

    [Fact]
    public void RrfMerger_Uses_Deterministic_TieBreaking_By_MinRank_Then_Ordinal_Id()
    {
        // Level 2 tie-breaker: equal Score (0.5 == 0.5), smaller MinRank wins even when Id is lexicographically later.
        var lexicalWeighted = new[]
        {
            new RankedCandidate("x1", 3.0),
            new RankedCandidate("x2", 2.0),
            new RankedCandidate("aaa", 1.0) // rank 3 -> 2.0 / (1 + 3) = 0.5
        };
        var vectorWeighted = new[]
        {
            new RankedCandidate("zzz", 0.99) // rank 1 -> 1.0 / (1 + 1) = 0.5
        };

        var weightedHits = RrfMerger.Merge(
            lexicalWeighted,
            vectorWeighted,
            k: 1,
            lexicalWeight: 2.0,
            vectorWeight: 1.0);

        // x1 (rank 1, score 1.0), x2 (rank 2, score 2/3), zzz (minRank 1, score 0.5), aaa (minRank 3, score 0.5)
        weightedHits.Select(h => h.Id).Should().Equal(new[] { "x1", "x2", "zzz", "aaa" });

        // Level 3 tie-breaker: equal Score and equal MinRank -> Ordinal Id order.
        var lexicalSymmetric = new[]
        {
            new RankedCandidate("b-node", 1.0),
            new RankedCandidate("a-node", 0.5)
        };
        var vectorSymmetric = new[]
        {
            new RankedCandidate("a-node", 1.0),
            new RankedCandidate("b-node", 0.5)
        };

        var symmetricHits = RrfMerger.Merge(lexicalSymmetric, vectorSymmetric, k: 60);
        symmetricHits.Select(h => h.Id).Should().Equal(new[] { "a-node", "b-node" });
    }

    [Fact]
    public void RrfMerger_Validates_K_And_Limit()
    {
        var empty = Array.Empty<RankedCandidate>();

        Action invalidK = () => RrfMerger.Merge(empty, empty, k: 0);
        Action negativeK = () => RrfMerger.Merge(empty, empty, k: -5);
        Action negativeLimit = () => RrfMerger.Merge(empty, empty, limit: -1);

        invalidK.Should().Throw<ArgumentOutOfRangeException>();
        negativeK.Should().Throw<ArgumentOutOfRangeException>();
        negativeLimit.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static float[] CreateDeterministicVector(int dimensions, float seedOffset)
    {
        var vector = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            vector[i] = MathF.Sin((i * 0.17f) + seedOffset) + (0.25f * MathF.Cos((i * 0.07f) - seedOffset));
        }

        return vector;
    }
}