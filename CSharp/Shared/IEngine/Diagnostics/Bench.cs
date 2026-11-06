using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Barotrauma;
using IEngine.Core;
using Microsoft.Xna.Framework;

namespace IEngine.Diagnostics;

static class Bench
{
    const int SettleTicks = 90;

    sealed record Window(int Cycle, string Mode, double WallSeconds, SampleStats[] Phases, int Gc0, int Gc1, int Gc2, long AllocatedBytes, string Scene, Dictionary<string, double> Counters, string WorstTicks);

    static readonly List<Window> windows = new();
    static string[] modes;
    static int cycles, cycle, modeIndex, settle;
    static double seconds, windowStart;
    static long wallStart;
    static long allocStart;
    static int gc0, gc1, gc2;
    static bool running;
    static float[] scratch = Array.Empty<float>();

    public static void Arm() => Probes.TickEnded += Tick;
    public static void Disarm() => Probes.TickEnded -= Tick;

    public static string Start(int cycleCount, double windowSeconds, string[] modeList)
    {
        if (running) { return "bench already running"; }
        windows.Clear();
        modes = modeList;
        cycles = cycleCount;
        seconds = windowSeconds;
        cycle = 0;
        modeIndex = 0;
        running = true;
        Sequencer.Blocking = true;
        Progress($"start {cycles}x{string.Join("/", modes)}x{seconds}s features: {Runtime.Describe()}");
        Enter();
        return $"bench: {cycles} cycles x {modes.Length} modes x {seconds}s";
    }

    static string CurrentMode => modes[cycle % 2 == 0 ? modeIndex : modes.Length - 1 - modeIndex];

    static void Enter()
    {
        Runtime.SetMode(CurrentMode);
        settle = SettleTicks;
    }

    static void Tick()
    {
        if (!running) { return; }
        if (settle > 0)
        {
            if (--settle == 0)
            {
                Probes.Recorder.Begin();
                windowStart = Timing.TotalTime;
                wallStart = Stopwatch.GetTimestamp();
                allocStart = GC.GetTotalAllocatedBytes(false);
                gc0 = GC.CollectionCount(0);
                gc1 = GC.CollectionCount(1);
                gc2 = GC.CollectionCount(2);
                EngineCounters.Begin();
            }
            return;
        }
        if (Timing.TotalTime - windowStart < seconds) { return; }
        Probes.Recorder.Stop();
        windows.Add(Capture());
        Progress($"window {windows.Count} mode {windows[^1].Mode} frames {windows[^1].Phases[(int)Phase.Frame].Count} ticks {windows[^1].Phases[(int)Phase.Tick].Count}");
        if (++modeIndex == modes.Length)
        {
            modeIndex = 0;
            if (++cycle == cycles) { Finish(); return; }
        }
        Enter();
    }

    static Window Capture()
    {
        // Stop measurement before sorting samples and constructing the report.
        double wallSeconds = Stopwatch.GetElapsedTime(wallStart).TotalSeconds;
        long allocated = GC.GetTotalAllocatedBytes(false) - allocStart;
        int collected0 = GC.CollectionCount(0) - gc0;
        int collected1 = GC.CollectionCount(1) - gc1;
        int collected2 = GC.CollectionCount(2) - gc2;
        var rec = Probes.Recorder;
        if (scratch.Length < Math.Max(rec.Count, rec.TickCount)) { scratch = new float[Math.Max(rec.Count, rec.TickCount)]; }
        var counters = EngineCounters.End(rec.Count);
        var stats = new SampleStats[(int)Phase.Count];
        for (int i = 0; i < stats.Length; i++) { stats[i] = SampleStats.Of(PhaseRecorder.PerFrame((Phase)i) ? rec.Samples((Phase)i) : rec.TickSamples((Phase)i), scratch); }
        return new Window(cycle, CurrentMode, wallSeconds, stats,
            collected0, collected1, collected2, allocated, Census.Brief(), counters, WorstTicks(rec, 3));
    }

    static string WorstTicks(PhaseRecorder rec, int k)
    {
        var tick = rec.TickSamples(Phase.Tick);
        var picked = new List<int>();
        for (int n = 0; n < k; n++)
        {
            int best = -1;
            for (int i = 0; i < tick.Length; i++) { if (!picked.Contains(i) && (best < 0 || tick[i] > tick[best])) { best = i; } }
            if (best < 0) { break; }
            picked.Add(best);
        }
        var sb = new StringBuilder();
        foreach (int i in picked)
        {
            sb.Append(CultureInfo.InvariantCulture, $"[{tick[i]:F1}ms:");
            for (int p = 0; p < (int)Phase.Count; p++)
            {
                if ((Phase)p is Phase.Tick || PhaseRecorder.PerFrame((Phase)p)) { continue; }
                float v = rec.TickSamples((Phase)p)[i];
                if (v >= 0.5f) { sb.Append(CultureInfo.InvariantCulture, $" {(Phase)p}={v:F1}"); }
            }
            sb.Append("] ");
        }
        return sb.ToString();
    }

    internal static string OutDir() => Environment.GetEnvironmentVariable("IE_OUT") is { Length: > 0 } o ? o : Path.Combine("IEngine", "out");

    static void Progress(string text)
    {
        try
        {
            Directory.CreateDirectory(OutDir());
            File.AppendAllText(Path.Combine(OutDir(), "bench_progress.txt"), $"{DateTime.Now:O} {text}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    static void Finish()
    {
        running = false;
        Runtime.SetMode(Settings.Mode);
        string dir = OutDir();
        Directory.CreateDirectory(dir);
        string side = GameMain.NetworkMember is { IsServer: true } || IsDedicated ? "server" : "client";
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string csv = Path.Combine(dir, $"bench_{side}_{stamp}.csv");
        File.WriteAllText(csv, Csv());
        var report = new StringBuilder(BenchSummary.Render(Rows(), modes));
        report.Append("worst ticks per window:\n");
        foreach (Window w in windows) { report.Append(CultureInfo.InvariantCulture, $"  {w.Cycle} {w.Mode}: {w.WorstTicks}\n"); }
        report.Append(Census.Full());
        foreach (Feature f in Runtime.Features) { report.Append(f.Name).Append(": ").Append(f.Installed ? "installed " : "unavailable " + f.Fault).Append(f.Report()).Append('\n'); }
        string summary = report.ToString();
        File.WriteAllText(Path.Combine(dir, $"bench_{side}_{stamp}.txt"), summary);
        foreach (string line in summary.Split('\n')) { DebugConsole.NewMessage(line, Color.LightGreen); }
        DebugConsole.NewMessage($"IEngine bench written to {csv}", Color.LightGreen);
        Sequencer.Blocking = false;
        Sequencer.Next();
    }

#if SERVER
    const bool IsDedicated = true;
#else
    const bool IsDedicated = false;
#endif

    static string Csv()
    {
        var sb = new StringBuilder("cycle,mode,wall_s,ticks,gc0,gc1,gc2,alloc_mb_s,phase,mean_ms,p50_ms,p95_ms,p99_ms,p999_ms,max_ms,worst1_ms,worst01_ms,scene\n");
        foreach (Window w in windows)
        {
            string head = string.Create(CultureInfo.InvariantCulture, $"{w.Cycle},{w.Mode},{w.WallSeconds:F2}");
            for (int i = 0; i < w.Phases.Length; i++)
            {
                SampleStats s = w.Phases[i];
                sb.Append(CultureInfo.InvariantCulture, $"{head},{s.Count},{w.Gc0},{w.Gc1},{w.Gc2},{w.AllocatedBytes / w.WallSeconds / 1048576.0:F3},{(Phase)i},{s.Mean:F4},{s.P50:F4},{s.P95:F4},{s.P99:F4},{s.P999:F4},{s.Max:F4},{s.Worst1:F4},{s.Worst01:F4},\"{w.Scene}\"\n");
            }
            foreach (var kv in w.Counters)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{head},0,{w.Gc0},{w.Gc1},{w.Gc2},0,\"counter:{kv.Key}\",{kv.Value:F4},,,,,,,,\n");
            }
        }
        return sb.ToString();
    }

    internal static IReadOnlyList<(string Mode, SampleStats[] Phases, double AllocMbPerSec, int Gc0, Dictionary<string, double> Counters)> Rows()
    {
        var rows = new List<(string, SampleStats[], double, int, Dictionary<string, double>)>();
        foreach (Window w in windows) { rows.Add((w.Mode, w.Phases, w.AllocatedBytes / w.WallSeconds / 1048576.0, w.Gc0, w.Counters)); }
        return rows;
    }
}
