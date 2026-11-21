using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SecondBrain.Infrastructure.Indexing;

/// <summary>
/// Deterministic SIMD-accelerated vector similarity and binary serialization helpers.
/// </summary>
public static class SimdCosine
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vector lengths must match ({a.Length} != {b.Length}).", nameof(b));
        }

        int width = Vector<float>.Count;
        Vector<float> acc = Vector<float>.Zero;
        int i = 0;

        for (; i <= a.Length - width; i += width)
        {
            var va = new Vector<float>(a.Slice(i));
            var vb = new Vector<float>(b.Slice(i));
            acc += va * vb;
        }

        float sum = Vector.Sum(acc);
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vector lengths must match ({a.Length} != {b.Length}).", nameof(b));
        }

        if (a.IsEmpty)
        {
            return 0f;
        }

        int width = Vector<float>.Count;
        Vector<float> accDot = Vector<float>.Zero;
        Vector<float> accNormA = Vector<float>.Zero;
        Vector<float> accNormB = Vector<float>.Zero;
        int i = 0;

        for (; i <= a.Length - width; i += width)
        {
            var va = new Vector<float>(a.Slice(i));
            var vb = new Vector<float>(b.Slice(i));
            accDot += va * vb;
            accNormA += va * va;
            accNormB += vb * vb;
        }

        double dot = Vector.Sum(accDot);
        double normASq = Vector.Sum(accNormA);
        double normBSq = Vector.Sum(accNormB);

        for (; i < a.Length; i++)
        {
            double x = a[i];
            double y = b[i];
            dot += x * y;
            normASq += x * x;
            normBSq += y * y;
        }

        if (normASq <= 0.0 || normBSq <= 0.0)
        {
            return 0f;
        }

        double denom = Math.Sqrt(normASq) * Math.Sqrt(normBSq);
        if (denom <= 0.0)
        {
            return 0f;
        }

        return Math.Clamp((float)(dot / denom), -1f, 1f);
    }

    public static float CosineNormalized(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        return Math.Clamp(Dot(a, b), -1f, 1f);
    }

    public static float[] NormalizeL2(ReadOnlySpan<float> vector)
    {
        int width = Vector<float>.Count;
        Vector<float> accNorm = Vector<float>.Zero;
        int i = 0;

        for (; i <= vector.Length - width; i += width)
        {
            var v = new Vector<float>(vector.Slice(i));
            accNorm += v * v;
        }

        double normSq = Vector.Sum(accNorm);
        for (; i < vector.Length; i++)
        {
            double x = vector[i];
            normSq += x * x;
        }

        if (normSq <= 0.0 || double.IsNaN(normSq))
        {
            throw new InvalidOperationException("Cannot L2-normalize a vector with zero norm.");
        }

        float invNorm = (float)(1.0 / Math.Sqrt(normSq));
        var invNormVec = new Vector<float>(invNorm);
        var normalized = new float[vector.Length];

        int j = 0;
        for (; j <= vector.Length - width; j += width)
        {
            var v = new Vector<float>(vector.Slice(j));
            (v * invNormVec).CopyTo(normalized.AsSpan(j));
        }

        for (; j < vector.Length; j++)
        {
            normalized[j] = vector[j] * invNorm;
        }

        return normalized;
    }

    public static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        return MemoryMarshal.AsBytes(vector).ToArray();
    }

    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"Byte span length ({bytes.Length}) must be a multiple of {sizeof(float)}.",
                nameof(bytes));
        }

        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }
}