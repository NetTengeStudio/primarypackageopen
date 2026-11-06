using System;
using System.Collections.Generic;
using Barotrauma;
using IEngine.Core;
using IEngine.Execution;
using PrimaryPackage.Core;

namespace IEngine.Simulation;

static class Dormancy
{
    public const byte ByContainer = 1, BySpatial = 2, All = 255;

    public static readonly SlotBitSet Dormant = new(), Probe = new();
    static byte[] reasons = new byte[8192], streak = new byte[8192], dirty = new byte[8192];
    static double[] since = new double[8192];

    public static double SimTime;
    public static float StepDt;
    static int[] generation = new int[8192];
    static readonly System.Collections.Generic.PriorityQueue<long, double> timers = new();
    public static long TimedWakes;
    public static Item Probing;
    public static bool Dirty;
    public static readonly System.Collections.Generic.Dictionary<string, int> DirtyCauses = new();

    public static void MarkDirty(string cause)
    {
        if (Dirty || Probing == null) { Dirty |= Probing != null; return; }
        Dirty = true;
        string key = $"{Probing.Prefab.Identifier.Value}: {cause}";
        DirtyCauses[key] = DirtyCauses.GetValueOrDefault(key) + 1;
    }
    public static int Count;
    public static long Sleeps, Wakes, CatchUps;
    public static Action<Item, double> CatchUp;
    public static readonly string[] SourceNames = { "other", "inventory", "condition", "property", "tags", "component active", "lua", "status list", "isactive", "spatial", "timer" };
    public static readonly long[] WakesBySource = new long[SourceNames.Length];
    public static int Source;

    static void Ensure(int slot)
    {
        if (slot >= reasons.Length)
        {
            int n = Math.Max(slot + 1, reasons.Length * 2);
            Array.Resize(ref reasons, n);
            Array.Resize(ref streak, n);
            Array.Resize(ref dirty, n);
            Array.Resize(ref generation, n);
            Array.Resize(ref since, n);
        }
        Dormant.EnsureCapacity(slot + 1);
        Probe.EnsureCapacity(slot + 1);
    }

    public static void OnAssigned(int slot)
    {
        SpatialCore.OnAssigned(slot);
        Ensure(slot);
        reasons[slot] = 0;
        streak[slot] = 0;
        Dormant.Clear(slot);
        Probe.Set(slot);
    }

    public static void OnFreed(int slot)
    {
        if (slot >= reasons.Length) { return; }
        generation[slot]++;
        if (reasons[slot] != 0) { Count--; }
        reasons[slot] = 0;
        streak[slot] = 0;
        Dormant.Clear(slot);
        Probe.Clear(slot);
    }

    public static byte ReasonsOf(int slot) => slot < reasons.Length ? reasons[slot] : (byte)0;

    public static ref byte Streak(int slot) => ref streak[slot];

    public static ref byte DirtyStreak(int slot) => ref dirty[slot];

    public static bool TrySleep(int slot, byte reason, double wakeAfter = double.PositiveInfinity)
    {
        byte r = reasons[slot];
        if ((r & reason) != 0) { return false; }
        Item item = ItemRegistry.Items[slot];
        if (item == null || !ItemUpdatePolicy.CanSkipIdle(item)) { return false; }
        if (r == 0)
        {
            Count++;
            Dormant.Set(slot);
        }
        if (reason == ByContainer)
        {
            double t = SimTime + StepDt;
            since[slot] = t;
            if (!double.IsPositiveInfinity(wakeAfter)) { timers.Enqueue(((long)slot << 32) | (uint)generation[slot], t + wakeAfter - StepDt); }
        }
        reasons[slot] = (byte)(r | reason);
        streak[slot] = 0;
        Probe.Clear(slot);
        Sleeps++;
        return true;
    }

    public static void Wake(Item item, byte mask = All)
    {
        if (item != null && ItemRegistry.TryGetSlot(item, out int slot)) { WakeSlot(slot, item, mask); }
    }

    public static void ProcessTimers()
    {
        double now = SimTime + 1e-4;
        Item[] items = ItemRegistry.Items;
        while (timers.TryPeek(out long key, out double due) && due <= now)
        {
            timers.Dequeue();
            int slot = (int)(key >> 32);
            if (slot >= generation.Length || generation[slot] != (int)key || (reasons[slot] & ByContainer) == 0 || items[slot] is not { } item) { continue; }
            Source = 10;
            TimedWakes++;
            WakeSlot(slot, item, ByContainer);
            Source = 0;
        }
    }

    public static void WakeSlot(int slot, Item item, byte mask)
    {
        if (slot >= reasons.Length) { return; }
        byte r = reasons[slot];
        if ((r & mask) == 0) { return; }
        Probe.Set(slot);
        dirty[slot] = 0;
        byte left = (byte)(r & ~mask);
        if ((r & ByContainer) != 0 && (left & ByContainer) == 0)
        {
            generation[slot]++;
            CatchUp?.Invoke(item, SimTime - since[slot]);
            CatchUps++;
        }
        if ((r & BySpatial) != 0 && (left & BySpatial) == 0 && Source != 9 && SpatialCore.On) { SpatialCore.OnEventWake(slot); }
        reasons[slot] = left;
        streak[slot] = 0;
        if (left != 0) { return; }
        Dormant.Clear(slot);
        Count--;
        Wakes++;
        WakesBySource[Source]++;
    }

    public static void WakeAll(byte mask)
    {
        Item[] items = ItemRegistry.Items;
        for (int s = Dormant.Next(0); s >= 0; s = Dormant.Next(s + 1))
        {
            if (items[s] is { } item) { WakeSlot(s, item, mask); }
        }
    }

    public static void Reset()
    {
        WakeAll(All);
        Dormant.ClearAll();
        Probe.ClearAll();
        Array.Clear(reasons);
        Array.Clear(streak);
        Array.Clear(dirty);
        timers.Clear();
        Count = 0;
        Probing = null;
    }

    public static void OnIsActiveWritten(int slot, bool changed)
    {
        if (changed) { MarkDirty("IsActive"); }
        if (!Dormant.Get(slot)) { return; }
        Source = 8;
        WakeSlot(slot, ItemRegistry.Items[slot], All);
        Source = 0;
    }

    public static string Report()
    {
        var sb = new System.Text.StringBuilder($"dormant={Count} sleeps={Sleeps} wakes={Wakes} timedWakes={TimedWakes} pendingTimers={timers.Count} catchups={CatchUps} simTime={SimTime:F0} wakeSources:");
        for (int i = 0; i < SourceNames.Length; i++) { if (WakesBySource[i] > 0) { sb.Append(' ').Append(SourceNames[i]).Append('=').Append(WakesBySource[i]); } }
        return sb.ToString();
    }
}
