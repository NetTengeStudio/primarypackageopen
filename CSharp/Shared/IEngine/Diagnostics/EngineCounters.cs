using System;
using System.Collections.Generic;
using System.Diagnostics;
using Barotrauma;
using HarmonyLib;

namespace IEngine.Diagnostics;

static class EngineCounters
{
    static readonly Dictionary<string, int> index = new(StringComparer.Ordinal);
    static readonly List<string> names = new();
    static long[] ticks = new long[64];
    static bool recording;

    public static void Install(Harmony harmony) =>
        harmony.Patch(AccessTools.Method(typeof(PerformanceCounter), nameof(PerformanceCounter.AddElapsedTicks), new[] { typeof(string), typeof(long) }),
            prefix: new HarmonyMethod(typeof(EngineCounters), nameof(Add)) { priority = Priority.First });

    static void Add(string identifier, long ticks)
    {
        if (!recording || identifier == null) { return; }
        if (!index.TryGetValue(identifier, out int i))
        {
            i = names.Count;
            index.Add(identifier, i);
            names.Add(identifier);
            if (i == EngineCounters.ticks.Length) { Array.Resize(ref EngineCounters.ticks, i * 2); }
        }
        EngineCounters.ticks[i] += ticks;
    }

    public static void Begin()
    {
        Array.Clear(ticks);
        recording = true;
    }

    public static Dictionary<string, double> End(int frames)
    {
        recording = false;
        var result = new Dictionary<string, double>(names.Count, StringComparer.Ordinal);
        double scale = 1000.0 / Stopwatch.Frequency / Math.Max(1, frames);
        for (int i = 0; i < names.Count; i++) { if (ticks[i] != 0) { result[names[i]] = ticks[i] * scale; } }
        return result;
    }
}
