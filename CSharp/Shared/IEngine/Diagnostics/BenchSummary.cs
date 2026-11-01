using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using IEngine.Core;

namespace IEngine.Diagnostics;

static class BenchSummary
{
    static readonly Phase[] Shown =
    {
        Phase.Frame, Phase.Tick, Phase.MapEntity, Phase.Power, Phase.Characters, Phase.Animation, Phase.StatusEffects,
        Phase.Ragdolls, Phase.Physics, Phase.Network, Phase.Draw, Phase.Present
    };

    public static string Render(IReadOnlyList<(string Mode, SampleStats[] Phases, double AllocMbPerSec, int Gc0, Dictionary<string, double> Counters)> rows, string[] modes)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"IEngine bench, {rows.Count} windows, modes {string.Join(" / ", modes)}; Frame/Draw/Present per frame, other phases per simulation tick\n");
        sb.Append("phase          ");
        foreach (string m in modes) { sb.Append(CultureInfo.InvariantCulture, $"| {m,-8} mean  p95   p99   [min..max of window means] "); }
        sb.Append("| delta mean\n");
        foreach (Phase p in Shown)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{p,-15}");
            double first = double.NaN, last = double.NaN;
            foreach (string m in modes)
            {
                var means = new List<double>();
                var p95 = new List<double>();
                var p99 = new List<double>();
                foreach (var r in rows)
                {
                    if (r.Mode != m) { continue; }
                    SampleStats s = r.Phases[(int)p];
                    means.Add(s.Mean);
                    p95.Add(s.P95);
                    p99.Add(s.P99);
                }
                double mm = Median(means);
                if (double.IsNaN(first)) { first = mm; }
                last = mm;
                sb.Append(CultureInfo.InvariantCulture, $"| {mm,8:F3} {Median(p95),6:F2} {Median(p99),6:F2} [{Min(means):F3}..{Max(means):F3}] ");
            }
            sb.Append(CultureInfo.InvariantCulture, $"| {(first > 0 ? (last - first) / first * 100.0 : 0),6:F1}%\n");
        }
        foreach (Phase p in new[] { Phase.Frame, Phase.Tick })
        {
            sb.Append(CultureInfo.InvariantCulture, $"{p} tails     ");
            foreach (string m in modes)
            {
                var p999 = new List<double>();
                var w1 = new List<double>();
                var w01 = new List<double>();
                var mean = new List<double>();
                foreach (var r in rows)
                {
                    if (r.Mode != m) { continue; }
                    SampleStats s = r.Phases[(int)p];
                    p999.Add(s.P999);
                    w1.Add(s.Worst1);
                    w01.Add(s.Worst01);
                    mean.Add(s.Mean);
                }
                double mm = Median(mean), a = Median(w1), b = Median(w01);
                sb.Append(CultureInfo.InvariantCulture, $"| {m}: p99.9 {Median(p999):F2} ms, worst1% {a:F2} ms, worst0.1% {b:F2} ms ");
                if (p == Phase.Frame && mm > 0) { sb.Append(CultureInfo.InvariantCulture, $"fps {1000 / mm:F0} / 1%low {1000 / a:F0} / 0.1%low {1000 / b:F0} "); }
            }
            sb.Append('\n');
        }
        var keys = new HashSet<string>();
        foreach (var r in rows) { if (r.Counters != null) { keys.UnionWith(r.Counters.Keys); } }
        var ordered = new List<(string Key, double First)>();
        foreach (string k in keys)
        {
            var v = new List<double>();
            foreach (var r in rows) { if (r.Mode == modes[0]) { v.Add(r.Counters.GetValueOrDefault(k)); } }
            ordered.Add((k, Median(v)));
        }
        ordered.Sort((x, y) => y.First.CompareTo(x.First));
        if (ordered.Count > 0) { sb.Append("engine counters, ms per sample (median of windows):\n"); }
        foreach (var (k, _) in ordered)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {k,-40}");
            foreach (string m in modes)
            {
                var v = new List<double>();
                foreach (var r in rows) { if (r.Mode == m) { v.Add(r.Counters.GetValueOrDefault(k)); } }
                sb.Append(CultureInfo.InvariantCulture, $" {Median(v),9:F4}");
            }
            sb.Append('\n');
        }
        foreach (string m in modes)
        {
            var alloc = new List<double>();
            var gc = new List<double>();
            foreach (var r in rows) { if (r.Mode == m) { alloc.Add(r.AllocMbPerSec); gc.Add(r.Gc0); } }
            sb.Append(CultureInfo.InvariantCulture, $"{m}: alloc {Median(alloc):F2} MB/s, gen0 per window {Median(gc):F0}\n");
        }
        return sb.ToString();
    }

    static double Median(List<double> v)
    {
        if (v.Count == 0) { return double.NaN; }
        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) * 0.5;
    }

    static double Min(List<double> v) { double m = double.MaxValue; foreach (double x in v) { m = Math.Min(m, x); } return m; }
    static double Max(List<double> v) { double m = double.MinValue; foreach (double x in v) { m = Math.Max(m, x); } return m; }
}
