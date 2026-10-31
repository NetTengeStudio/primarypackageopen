using System;

namespace IEngine.Core;

public readonly record struct SampleStats(int Count, double Mean, float P50, float P95, float P99, float P999, float Max, double Worst1, double Worst01)
{
    public static SampleStats Of(ReadOnlySpan<float> samples, Span<float> scratch)
    {
        int n = samples.Length;
        if (n == 0) { return default; }
        Span<float> sorted = scratch[..n];
        samples.CopyTo(sorted);
        sorted.Sort();
        double sum = 0;
        for (int i = 0; i < n; i++) { sum += sorted[i]; }
        return new SampleStats(n, sum / n, Rank(sorted, 0.50), Rank(sorted, 0.95), Rank(sorted, 0.99), Rank(sorted, 0.999), sorted[n - 1], TailMean(sorted, 0.01), TailMean(sorted, 0.001));
    }

    static float Rank(ReadOnlySpan<float> sorted, double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];

    static double TailMean(ReadOnlySpan<float> sorted, double fraction)
    {
        int k = Math.Max(1, (int)(sorted.Length * fraction));
        double s = 0;
        for (int i = sorted.Length - k; i < sorted.Length; i++) { s += sorted[i]; }
        return s / k;
    }
}
