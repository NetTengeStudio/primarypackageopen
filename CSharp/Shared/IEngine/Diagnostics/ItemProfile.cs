using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace IEngine.Diagnostics;

static class ItemProfile
{
    public static bool On;
    static int ticksLeft, ticksTotal;
    static readonly Dictionary<Item, long> cost = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<Item, int> calls = new(ReferenceEqualityComparer.Instance);

    public static string Start(int ticks)
    {
        cost.Clear();
        calls.Clear();
        ticksLeft = ticksTotal = ticks;
        On = true;
        Sequencer.Blocking = true;
        return $"item profile for {ticks} full item ticks";
    }

    public static void Add(Item item, long ticks)
    {
        cost[item] = cost.GetValueOrDefault(item) + ticks;
        calls[item] = calls.GetValueOrDefault(item) + 1;
    }

    public static void EndItemTick()
    {
        if (!On || --ticksLeft > 0) { return; }
        On = false;
        string text = Report();
        try
        {
            Directory.CreateDirectory(Bench.OutDir());
            File.WriteAllText(Path.Combine(Bench.OutDir(), $"itemprof_{DateTime.Now:HHmmss}.txt"), text);
        }
        catch (IOException) { }
        foreach (string line in text.Split('\n').Take(60)) { DebugConsole.NewMessage(line, Color.LightGreen); }
        Sequencer.Blocking = false;
        Sequencer.Next();
    }

    static string State(Item item)
    {
        if (item.ParentInventory is CharacterInventory) { return "character inventory"; }
        if (item.ParentInventory != null)
        {
            int depth = 0;
            for (Item p = item.Container; p != null; p = p.Container) { depth++; }
            return depth > 1 ? "nested container" : "container";
        }
        if (item.body is { Enabled: true }) { return "free body"; }
        return "installed";
    }

    static List<Vector2> Viewers()
    {
        var list = new List<Vector2>();
        foreach (Character c in Character.CharacterList)
        {
            if (c.IsRemotePlayer || c == Character.Controlled) { list.Add(c.WorldPosition); }
        }
        if (list.Count == 0) { foreach (Character c in Character.CharacterList) { if (c.IsHuman && !c.IsDead) { list.Add(c.WorldPosition); } } }
#if CLIENT
        if (list.Count == 0 && GameMain.GameScreen?.Cam is { } cam) { list.Add(cam.Position); }
#endif
        return list;
    }

    static string Report()
    {
        double ms = 1000.0 / Stopwatch.Frequency / ticksTotal;
        var viewers = Viewers();
        double total = 0;
        var byPrefab = new Dictionary<string, (double Ms, int N)>();
        var byState = new Dictionary<string, (double Ms, int N)>();
        var byComponents = new Dictionary<string, (double Ms, int N)>();
        var farReason = new Dictionary<string, (double Ms, int N)>();
        var farPrefab = new Dictionary<string, (double Ms, int N)>();
        double[] distanceEdges = { 10, 20, 30, 50, 75, 100, 150, 200, double.MaxValue };
        var byDistance = new (double Ms, int N)[distanceEdges.Length];
        foreach (var kv in cost)
        {
            Item item = kv.Key;
            double v = kv.Value * ms;
            total += v;
            Bump(byPrefab, item.Prefab.Identifier.Value, v);
            Bump(byState, State(item), v);
            Bump(byComponents, item.updateableComponents.Count == 0 ? "(no updateable components)" : string.Join("+", item.updateableComponents.Select(c => c.GetType().Name).Distinct().OrderBy(n => n)), v);
            double d = double.MaxValue;
            foreach (Vector2 p in viewers) { d = Math.Min(d, Vector2.Distance(p, item.WorldPosition) / 100.0); }
            int b = 0;
            while (d > distanceEdges[b]) { b++; }
            if (d > Simulation.SpatialCore.Radius / 100.0 + 10)
            {
                string why = Simulation.SpatialCore.Explain(item);
                Bump(farReason, why, v);
                Bump(farPrefab, $"{item.Prefab.Identifier.Value} [{why}]", v);
            }
            byDistance[b].Ms += v;
            byDistance[b].N++;
        }
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"item profile: {ticksTotal} full ticks, {cost.Count} items updated, {total:F3} ms per full tick, viewers {viewers.Count}\n");
        Table(sb, "by state", byState, total, 10);
        sb.Append("by distance to nearest viewer (m):\n");
        double cum = 0;
        for (int i = 0; i < byDistance.Length; i++)
        {
            cum += byDistance[i].Ms;
            sb.Append(CultureInfo.InvariantCulture, $"  <= {(distanceEdges[i] == double.MaxValue ? "inf" : distanceEdges[i].ToString(CultureInfo.InvariantCulture)),-5} {byDistance[i].N,6} items {byDistance[i].Ms,8:F3} ms  cumulative {cum / Math.Max(total, 1e-9) * 100,5:F1}%\n");
        }
        if (farReason.Count > 0)
        {
            Table(sb, "updated beyond spatial radius, by reason", farReason, total, 10);
            Table(sb, "updated beyond spatial radius, by prefab", farPrefab, total, 30);
        }
        Table(sb, "by prefab", byPrefab, total, 40);
        Table(sb, "by updateable component set", byComponents, total, 30);
        return sb.ToString();
    }

    static void Bump(Dictionary<string, (double Ms, int N)> d, string key, double v)
    {
        var e = d.GetValueOrDefault(key);
        d[key] = (e.Ms + v, e.N + 1);
    }

    static void Table(StringBuilder sb, string title, Dictionary<string, (double Ms, int N)> d, double total, int top)
    {
        sb.Append(title).Append(":\n");
        foreach (var kv in d.OrderByDescending(k => k.Value.Ms).Take(top))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {kv.Key,-48} {kv.Value.N,6} items {kv.Value.Ms,8:F3} ms {kv.Value.Ms / Math.Max(total, 1e-9) * 100,5:F1}%  {kv.Value.Ms * 1000 / kv.Value.N,7:F2} us/item\n");
        }
    }
}
