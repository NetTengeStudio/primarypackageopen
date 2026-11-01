using System;
using System.Collections.Generic;
using System.Diagnostics;
using Barotrauma;
using Barotrauma.Items.Components;
using IEngine.Core;
using IEngine.Execution;
using Microsoft.Xna.Framework;

namespace IEngine.Simulation;

static class SpatialCore
{
    const float CellSize = 1600f;
    const double EventAwakeSeconds = 5.0;

    public static bool On;
    public static float Radius = 5000f, Hysteresis = 1000f, NpcRadius = 800f;
    public static bool BotsAsViewers;

    static readonly Dictionary<long, List<int>> cells = new();
    static readonly HashSet<long> awake = new(), inner = new(), outer = new(), scratch = new();
    static readonly List<Vector2> viewers = new(), npcs = new();
    static readonly Queue<(int Slot, double Until)> eventAwake = new();
    static readonly List<Submarine> subs = new();
    static readonly HashSet<int> pending = new();
    static readonly List<int> pendingOrder = new();
    static int[] parent = Array.Empty<int>(), islandAwake = Array.Empty<int>();
    static long[] cellOf = Array.Empty<long>();
    static bool[] pinned = Array.Empty<bool>(), indexed = Array.Empty<bool>();
    static List<int>[] members = Array.Empty<List<int>>();
    static bool dirty = true;
    public static long Rebuilds, Frozen, Thawed, EventRefreezes, Reindexed;
    public static CostMeter RebuildCost, TickCost;
    public static int IndexedCount;

    public static bool IsIndexed(Item item) => ItemRegistry.TryGetSlot(item, out int s) && s < indexed.Length && indexed[s];

    public static void MarkDirty() => dirty = true;

    public static void Start()
    {
        dirty = true;
        awake.Clear();
        eventAwake.Clear();
        pending.Clear();
        pendingOrder.Clear();
    }

    public static void Stop()
    {
        Dormancy.WakeAll(Dormancy.BySpatial);
        cells.Clear();
        awake.Clear();
        eventAwake.Clear();
        pending.Clear();
        pendingOrder.Clear();
    }

    public static void OnMoved(Item item)
    {
        if (!On || dirty) { return; }
        Enqueue(item);
        foreach (Item c in item.ContainedItems) { OnMoved(c); }
    }

    public static void OnAssigned(int slot)
    {
        if (On && !dirty && pending.Add(slot)) { pendingOrder.Add(slot); }
    }

    static void Enqueue(Item item)
    {
        if (ItemRegistry.TryGetSlot(item, out int s) && pending.Add(s)) { pendingOrder.Add(s); }
    }

    public static void WiringChanged(Item a, Item b)
    {
        if (!On || dirty || a == null || b == null) { return; }
        if (ItemRegistry.TryGetSlot(a, out int sa) && ItemRegistry.TryGetSlot(b, out int sb) && sa < indexed.Length && sb < indexed.Length && indexed[sa] && indexed[sb]) { Union(sa, sb); }
    }

    public static void BeginTick()
    {
        long t0 = Stopwatch.GetTimestamp();
        if (dirty)
        {
            Rebuild();
            RebuildCost.Add(Stopwatch.GetTimestamp() - t0);
        }
        else if (pendingOrder.Count > 0) { ProcessPending(); }
        CollectViewers();
        ComputeRegions();
        scratch.Clear();
        foreach (long c in awake) { if (!outer.Contains(c)) { scratch.Add(c); } }
        foreach (long c in scratch) { awake.Remove(c); ChangeCell(c, -1); }
        foreach (long c in inner) { if (awake.Add(c)) { ChangeCell(c, +1); } }
        RefreezeExpired();
        TickCost.Add(Stopwatch.GetTimestamp() - t0);
    }

    static void ProcessPending()
    {
        Item[] items = ItemRegistry.Items;
        EnsureCapacity(items.Length);
        foreach (int s in pendingOrder)
        {
            Unindex(s);
            if (items[s] is { Removed: false } item && Index(s, item))
            {
                Reindexed++;
                if ((Dormancy.ReasonsOf(s) & Dormancy.BySpatial) == 0) { eventAwake.Enqueue((s, Dormancy.SimTime + EventAwakeSeconds)); }
            }
        }
        pending.Clear();
        pendingOrder.Clear();
    }

    static void ChangeCell(long cell, int delta)
    {
        if (!cells.TryGetValue(cell, out List<int> list)) { return; }
        foreach (int slot in list)
        {
            int r = Find(slot);
            int before = islandAwake[r];
            islandAwake[r] = before + delta;
            if (before == 0 && delta > 0) { ThawIsland(r); }
            else if (before + delta == 0 && delta < 0) { FreezeIsland(r); }
        }
    }

    static void ThawIsland(int root)
    {
        Item[] items = ItemRegistry.Items;
        Dormancy.Source = 9;
        foreach (int s in members[root])
        {
            if (items[s] is not { } item || (Dormancy.ReasonsOf(s) & Dormancy.BySpatial) == 0) { continue; }
            Dormancy.WakeSlot(s, item, Dormancy.BySpatial);
            Thawed++;
        }
        Dormancy.Source = 0;
    }

    static void FreezeIsland(int root)
    {
        if (pinned[root]) { return; }
        foreach (int s in members[root])
        {
            if ((Dormancy.ReasonsOf(s) & Dormancy.BySpatial) != 0 || ItemRegistry.Items[s] == null) { continue; }
            if (Dormancy.TrySleep(s, Dormancy.BySpatial)) { Frozen++; }
        }
    }

    public static void OnEventWake(int slot) => eventAwake.Enqueue((slot, Dormancy.SimTime + EventAwakeSeconds));

    static void RefreezeExpired()
    {
        while (eventAwake.TryPeek(out var e) && e.Until <= Dormancy.SimTime)
        {
            eventAwake.Dequeue();
            if (e.Slot >= indexed.Length || !indexed[e.Slot]) { continue; }
            int r = Find(e.Slot);
            if (islandAwake[r] > 0 || pinned[r] || (Dormancy.ReasonsOf(e.Slot) & Dormancy.BySpatial) != 0 || ItemRegistry.Items[e.Slot] == null) { continue; }
            if (Dormancy.TrySleep(e.Slot, Dormancy.BySpatial)) { EventRefreezes++; }
        }
    }

    static void CollectViewers()
    {
        viewers.Clear();
        npcs.Clear();
#if SERVER
        if (GameMain.Server != null)
        {
            foreach (var c in GameMain.Server.ConnectedClients)
            {
                if (c.Character is { IsDead: false } ch) { viewers.Add(ch.WorldPosition); }
                else if (c.SpectatePos is Vector2 p) { viewers.Add(p); }
            }
        }
#else
        if (Character.Controlled is { } controlled) { viewers.Add(controlled.WorldPosition); }
        if (GameMain.GameScreen?.Cam is { } cam) { viewers.Add(cam.Position); }
#endif
        foreach (Character c in Character.CharacterList)
        {
            if (c.IsDead || c.Removed || c.IsRemotePlayer || c == Character.Controlled) { continue; }
            if (BotsAsViewers && c.IsHuman) { viewers.Add(c.WorldPosition); }
            else { npcs.Add(c.WorldPosition); }
        }
    }

    static void ComputeRegions()
    {
        inner.Clear();
        outer.Clear();
        foreach (Vector2 v in viewers)
        {
            AddDisc(v, Radius, inner);
            AddDisc(v, Radius + Hysteresis, outer);
        }
        foreach (Vector2 v in npcs)
        {
            AddDisc(v, NpcRadius, inner);
            AddDisc(v, NpcRadius + Hysteresis, outer);
        }
    }

    static void AddDisc(Vector2 world, float radius, HashSet<long> into)
    {
        AddDiscLocal(0, world, radius, into);
        for (int i = 0; i < subs.Count; i++) { AddDiscLocal(i + 1, world - subs[i].Position, radius, into); }
    }

    static void AddDiscLocal(int sub, Vector2 p, float radius, HashSet<long> into)
    {
        int x0 = (int)MathF.Floor((p.X - radius) / CellSize), x1 = (int)MathF.Floor((p.X + radius) / CellSize);
        int y0 = (int)MathF.Floor((p.Y - radius) / CellSize), y1 = (int)MathF.Floor((p.Y + radius) / CellSize);
        float r2 = radius * radius;
        for (int x = x0; x <= x1; x++)
        {
            float dx = Math.Max(0, Math.Max(x * CellSize - p.X, p.X - (x + 1) * CellSize));
            for (int y = y0; y <= y1; y++)
            {
                float dy = Math.Max(0, Math.Max(y * CellSize - p.Y, p.Y - (y + 1) * CellSize));
                if (dx * dx + dy * dy <= r2) { into.Add(Key(sub, x, y)); }
            }
        }
    }

    static long Key(int sub, int x, int y) => ((long)sub << 42) | ((long)(x & 0x1FFFFF) << 21) | (long)(y & 0x1FFFFF);

    static Item Anchor(Item item)
    {
        Item root = item;
        for (Item c = item.Container; c != null; c = c.Container) { root = c; }
        if (item.ParentInventory is CharacterInventory || root.ParentInventory is CharacterInventory) { return null; }
        if (root.body is { Enabled: true } || item.body is { Enabled: true }) { return null; }
        return root;
    }

    static bool Pinned(Item item)
    {
        foreach (ItemComponent ic in item.components)
        {
            if (ic is Reactor or OxygenGenerator or Vent or Pump or PowerContainer or PowerTransfer or Engine or Steering or WifiComponent or DockingPort) { return true; }
        }
        return false;
    }

    static void EnsureCapacity(int n)
    {
        if (parent.Length >= n) { return; }
        int old = parent.Length;
        Array.Resize(ref parent, n);
        Array.Resize(ref islandAwake, n);
        Array.Resize(ref cellOf, n);
        Array.Resize(ref pinned, n);
        Array.Resize(ref indexed, n);
        Array.Resize(ref members, n);
        for (int s = old; s < n; s++) { parent[s] = s; }
    }

    static bool Index(int s, Item item)
    {
        if (Anchor(item) is not { } root) { return false; }
        int sub = root.Submarine != null ? subs.IndexOf(root.Submarine) + 1 : 0;
        Vector2 p = root.Submarine != null ? root.Position : root.WorldPosition;
        long key = Key(sub, (int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize));
        cellOf[s] = key;
        indexed[s] = true;
        IndexedCount++;
        if (!cells.TryGetValue(key, out List<int> cell)) { cells[key] = cell = new List<int>(); }
        cell.Add(s);
        int r = Find(s);
        (members[r] ??= new List<int>()).Add(s);
        if (Pinned(item)) { pinned[r] = true; }
        if (awake.Contains(key)) { islandAwake[r]++; }
        if (root != item && ItemRegistry.TryGetSlot(root, out int rs) && rs < indexed.Length && indexed[rs]) { Union(s, rs); }
        if (item.connections != null)
        {
            foreach (Connection c in item.connections.Values)
            {
                if (c.IsPower) { continue; }
                foreach (Wire w in c.wires)
                {
                    if (w.OtherConnection(c)?.Item is { } other && ItemRegistry.TryGetSlot(other, out int os) && os < indexed.Length && indexed[os]) { Union(s, os); }
                }
            }
        }
        return true;
    }

    static void Unindex(int s)
    {
        if (s >= indexed.Length || !indexed[s]) { return; }
        indexed[s] = false;
        IndexedCount--;
        if (cells.TryGetValue(cellOf[s], out List<int> cell) && cell.IndexOf(s) is int at and >= 0)
        {
            cell[at] = cell[^1];
            cell.RemoveAt(cell.Count - 1);
        }
        int r = Find(s);
        if (awake.Contains(cellOf[s])) { islandAwake[r]--; }
        members[r]?.Remove(s);
        if (ItemRegistry.Items[s] is { } item && (Dormancy.ReasonsOf(s) & Dormancy.BySpatial) != 0)
        {
            Dormancy.Source = 9;
            Dormancy.WakeSlot(s, item, Dormancy.BySpatial);
            Dormancy.Source = 0;
        }
    }

    static void Rebuild()
    {
        dirty = false;
        Rebuilds++;
        Dormancy.WakeAll(Dormancy.BySpatial);
        pending.Clear();
        pendingOrder.Clear();
        int n = ItemRegistry.Items.Length;
        EnsureCapacity(n);
        foreach (var list in cells.Values) { list.Clear(); }
        subs.Clear();
        subs.AddRange(Submarine.Loaded);
        IndexedCount = 0;
        for (int s = 0; s < n; s++)
        {
            parent[s] = s;
            islandAwake[s] = 0;
            pinned[s] = false;
            indexed[s] = false;
            members[s]?.Clear();
        }
        Item[] items = ItemRegistry.Items;
        for (int s = 0; s < n; s++)
        {
            if (items[s] is { Removed: false } item && item.Container == null) { Index(s, item); }
        }
        for (int s = 0; s < n; s++)
        {
            if (items[s] is { Removed: false } item && item.Container != null) { Index(s, item); }
        }
        for (int s = 0; s < n; s++)
        {
            if (indexed[s] && Find(s) == s && islandAwake[s] == 0) { FreezeIsland(s); }
        }
    }

    static int Find(int s)
    {
        while (parent[s] != s)
        {
            parent[s] = parent[parent[s]];
            s = parent[s];
        }
        return s;
    }

    static void Union(int a, int b)
    {
        a = Find(a);
        b = Find(b);
        if (a == b) { return; }
        List<int> ma = members[a] ??= new List<int>(), mb = members[b] ??= new List<int>();
        if (ma.Count < mb.Count) { (a, b) = (b, a); (ma, mb) = (mb, ma); }
        bool aAwake = islandAwake[a] > 0 || pinned[a], bAwake = islandAwake[b] > 0 || pinned[b];
        parent[b] = a;
        ma.AddRange(mb);
        mb.Clear();
        islandAwake[a] += islandAwake[b];
        pinned[a] |= pinned[b];
        if (aAwake && !bAwake) { ThawIsland(a); }
        else if (bAwake && !aAwake) { ThawIsland(a); }
    }

    public static string Explain(Item item)
    {
        if (!On) { return "spatial off"; }
        if (!ItemRegistry.TryGetSlot(item, out int s) || s >= indexed.Length || !indexed[s]) { return Anchor(item) == null ? "not anchored" : "not indexed yet"; }
        int r = Find(s);
        if (pinned[r]) { return Pinned(item) ? "pinned (life support)" : "island pinned by wire to life support"; }
        if (islandAwake[r] > 0) { return awake.Contains(cellOf[s]) ? "in awake cell" : $"island awake by wire ({members[r].Count} members)"; }
        return "event awake";
    }

    static int FrozenNow()
    {
        int n = 0;
        for (int s = Dormancy.Dormant.Next(0); s >= 0; s = Dormancy.Dormant.Next(s + 1)) { if ((Dormancy.ReasonsOf(s) & Dormancy.BySpatial) != 0) { n++; } }
        return n;
    }

    public static string Report() =>
        $"radius={Radius / 100:F0}m hysteresis={Hysteresis / 100:F0}m npcRadius={NpcRadius / 100:F0}m botsAsViewers={BotsAsViewers} viewers={viewers.Count} npcs={npcs.Count} indexed={IndexedCount} awakeCells={awake.Count} cells={cells.Count} frozenNow={FrozenNow()} frozen={Frozen} thawed={Thawed} eventRefreezes={EventRefreezes} reindexed={Reindexed} rebuilds={Rebuilds} rebuildCost={RebuildCost} tickCost={TickCost}";
}
