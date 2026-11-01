using System.Diagnostics;

namespace IEngine.Core;

public struct CostMeter
{
    public long Count, TotalTicks, MaxTicks;

    public void Add(long ticks)
    {
        Count++;
        TotalTicks += ticks;
        if (ticks > MaxTicks) { MaxTicks = ticks; }
    }

    public override string ToString() => Count == 0 ? "0" : $"{Count}x avg {TotalTicks * 1000.0 / Stopwatch.Frequency / Count:F3}ms max {MaxTicks * 1000.0 / Stopwatch.Frequency:F3}ms";
}
