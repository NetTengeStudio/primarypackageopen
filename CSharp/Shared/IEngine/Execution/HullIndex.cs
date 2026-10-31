using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using IEngine.Core;

namespace IEngine.Execution;

static class HullIndex
{
    const int SweepPerTick = 96;

    static readonly Dictionary<Hull, SlotBitSet> members = new(ReferenceEqualityComparer.Instance);
    static readonly Stack<SlotBitSet> pool = new();
    static Hull[] hullOf = new Hull[8192];
    static int sweepCursor;
    public static bool On;
    public static long Moves, Missed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreHull(Item item, Hull value)
    {
        item.currentHull = value;
        if (On && ItemRegistry.TryGetSlot(item, out int slot)) { Place(slot, value); }
    }

    static void Place(int slot, Hull value)
    {
        Hull old = hullOf[slot];
        if (ReferenceEquals(old, value)) { return; }
        if (old != null && members.TryGetValue(old, out SlotBitSet was)) { was.Clear(slot); }
        hullOf[slot] = value;
        if (value != null) { Members(value).Set(slot); }
        Moves++;
    }

    static SlotBitSet Members(Hull hull)
    {
        if (!members.TryGetValue(hull, out SlotBitSet set))
        {
            set = pool.Count > 0 ? pool.Pop() : new SlotBitSet();
            members.Add(hull, set);
        }
        set.EnsureCapacity(hullOf.Length);
        return set;
    }

    public static SlotBitSet Of(Hull hull) => members.TryGetValue(hull, out SlotBitSet set) ? set : null;

    public static void OnAssigned(int slot, Item item)
    {
        if (!On) { return; }
        Ensure(slot);
        hullOf[slot] = null;
        Place(slot, item.currentHull);
    }

    public static void OnFreed(int slot)
    {
        if (!On || slot >= hullOf.Length) { return; }
        Hull old = hullOf[slot];
        if (old != null && members.TryGetValue(old, out SlotBitSet set)) { set.Clear(slot); }
        hullOf[slot] = null;
    }

    static void Ensure(int slot)
    {
        if (slot < hullOf.Length) { return; }
        Array.Resize(ref hullOf, Math.Max(slot + 1, hullOf.Length * 2));
        foreach (SlotBitSet set in members.Values) { set.EnsureCapacity(hullOf.Length); }
    }

    public static void Clear()
    {
        foreach (SlotBitSet set in members.Values)
        {
            set.ClearAll();
            pool.Push(set);
        }
        members.Clear();
        Array.Clear(hullOf);
        sweepCursor = 0;
    }

    public static void Start()
    {
        On = true;
        Rebuild();
    }

    public static void Stop()
    {
        On = false;
        Clear();
    }

    public static void Rebuild()
    {
        Clear();
        if (!On || !ItemRegistry.Tracking) { return; }
        Item[] items = ItemRegistry.Items;
        for (int slot = 0; slot < items.Length; slot++)
        {
            if (items[slot] is { } item) { OnAssigned(slot, item); }
        }
    }

    public static void Sweep()
    {
        if (!On) { return; }
        Item[] items = ItemRegistry.Items;
        int assigned = ItemRegistry.SlotLimit;
        if (assigned == 0) { sweepCursor = 0; return; }
        int n = Math.Min(SweepPerTick, assigned);
        for (int i = 0; i < n; i++)
        {
            if (sweepCursor >= assigned) { sweepCursor = 0; }
            int slot = sweepCursor++;
            if (items[slot] is not { } item) { continue; }
            Ensure(slot);
            if (!ReferenceEquals(item.currentHull, hullOf[slot]))
            {
                Missed++;
                Place(slot, item.currentHull);
            }
        }
    }

    public static int Mismatches()
    {
        int bad = 0;
        Item[] items = ItemRegistry.Items;
        for (int slot = 0; slot < items.Length; slot++)
        {
            if (items[slot] is not { } item) { continue; }
            Hull h = item.currentHull;
            if (!ReferenceEquals(h, slot < hullOf.Length ? hullOf[slot] : null) || (h != null && Of(h)?.Get(slot) != true)) { bad++; }
        }
        return bad;
    }
}
