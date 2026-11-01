using System;
using System.Diagnostics;

namespace IEngine.Core;

public sealed class PhaseRecorder
{
    const int PhaseCount = (int)Phase.Count;
    static readonly double MsPerTimestamp = 1000.0 / Stopwatch.Frequency;

    readonly long[] open = new long[PhaseCount], tickOpen = new long[PhaseCount];
    readonly float[][] samples = new float[PhaseCount][], tickSamples = new float[PhaseCount][];
    readonly int capacity;
    int count, tickCount;

    public PhaseRecorder(int capacity)
    {
        this.capacity = capacity;
        for (int i = 0; i < PhaseCount; i++)
        {
            samples[i] = new float[capacity];
            tickSamples[i] = new float[capacity];
        }
    }

    public bool Recording { get; private set; }
    public int Count => count;
    public int TickCount => tickCount;

    public void Add(Phase phase, long startTimestamp) => AddTicks(phase, Stopwatch.GetTimestamp() - startTimestamp);

    public void AddTicks(Phase phase, long ticks)
    {
        open[(int)phase] += ticks;
        tickOpen[(int)phase] += ticks;
    }

    public void EndSimTick()
    {
        if (Recording && tickCount < capacity)
        {
            for (int i = 0; i < PhaseCount; i++) { tickSamples[i][tickCount] = (float)(tickOpen[i] * MsPerTimestamp); }
            tickCount++;
        }
        Array.Clear(tickOpen);
    }

    public void EndTick()
    {
        if (Recording && count < capacity)
        {
            for (int i = 0; i < PhaseCount; i++) { samples[i][count] = (float)(open[i] * MsPerTimestamp); }
            count++;
        }
        Array.Clear(open);
    }

    public void Begin()
    {
        count = tickCount = 0;
        Array.Clear(open);
        Array.Clear(tickOpen);
        Recording = true;
    }

    public void Stop() => Recording = false;

    public ReadOnlySpan<float> Samples(Phase phase) => samples[(int)phase].AsSpan(0, count);

    public ReadOnlySpan<float> TickSamples(Phase phase) => tickSamples[(int)phase].AsSpan(0, tickCount);

    public static bool PerFrame(Phase phase) => phase is Phase.Frame or Phase.Draw or Phase.Present;
}
