using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using IEngine.Core;

namespace IEngine.Execution;

static class ItemRegistry
{
    const int SweepPerTick = 96;

    static readonly Dictionary<Item, int> slotOf = new(ReferenceEqualityComparer.Instance);
    static Item[] items = new Item[8192];
    static readonly SlotBitSet active = new();
    static int nextSlot, live, sweepCursor;

    public static bool Tracking;
    public static long Rebuilds, Missed, Compactions;

    public static SlotBitSet Active => active;
    public static Item[] Items => items;
    // Slots in [0, SlotLimit) have been assigned at least once since rebuild.
    // The array capacity is deliberately larger and does not imply live items.
    public static int SlotLimit => nextSlot;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreIsActive(Item item, bool value)
    {
        bool was = item.IsActive;
        item.IsActive = value;
        if (!Tracking || (!value && Simulation.Dormancy.Probing == null) || !slotOf.TryGetValue(item, out int slot)) { return; }
        if (value) { active.Set(slot); }
        Simulation.Dormancy.OnIsActiveWritten(slot, was != value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetSlot(Item item, out int slot) => slotOf.TryGetValue(item, out slot);

    public static void OnListed(Item item)
    {
        if (!Tracking || slotOf.ContainsKey(item)) { return; }
        Assign(item);
    }

    public static void OnUnlisted(Item item)
    {
        if (!Tracking || !slotOf.Remove(item, out int slot)) { return; }
        // A removed item must not remain in a spatial island or a cell index.
        // DSU topology is reconstructed once on the next simulation tick.
        Simulation.SpatialCore.OnFreed(slot);
        items[slot] = null;
        active.Clear(slot);
        Simulation.Dormancy.OnFreed(slot);
        HullIndex.OnFreed(slot);
        live--;
    }

    static void Assign(Item item)
    {
        if (nextSlot == items.Length) { Array.Resize(ref items, items.Length * 2); }
        int slot = nextSlot++;
        active.EnsureCapacity(nextSlot);
        items[slot] = item;
        slotOf[item] = slot;
        live++;
        Simulation.Dormancy.OnAssigned(slot);
        HullIndex.OnAssigned(slot, item);
        if (item.IsActive) { active.Set(slot); }
    }

    public static void Start()
    {
        Tracking = true;
        Rebuild();
    }

    public static void Stop()
    {
        Simulation.Dormancy.Reset();
        HullIndex.Clear();
        Tracking = false;
        slotOf.Clear();
        Array.Clear(items);
        active.ClearAll();
        nextSlot = live = 0;
    }

    public static void Rebuild()
    {
        Simulation.Dormancy.Reset();
        Simulation.SpatialCore.MarkDirty();
        HullIndex.Clear();
        slotOf.Clear();
        Array.Clear(items, 0, nextSlot);
        active.ClearAll();
        nextSlot = live = 0;
        sweepCursor = 0;
        foreach (Item item in Item.ItemList) { if (!slotOf.ContainsKey(item)) { Assign(item); } }
        Rebuilds++;
    }

    public static void BeginTick()
    {
        var list = Item.ItemList;
        if (live != list.Count) { Rebuild(); return; }
        if (nextSlot > 4096 && nextSlot > live * 2) { Rebuild(); Compactions++; return; }
        HullIndex.Sweep();
        int n = Math.Min(SweepPerTick, list.Count);
        for (int i = 0; i < n; i++)
        {
            if (sweepCursor >= list.Count) { sweepCursor = 0; }
            Item item = list[sweepCursor++];
            if (item.IsActive && slotOf.TryGetValue(item, out int slot) && !active.Get(slot))
            {
                active.Set(slot);
                Missed++;
            }
        }
    }

    public static int Count => live;

    public static int VerifyCoverage()
    {
        int uncovered = 0;
        foreach (Item item in Item.ItemList)
        {
            if (item.IsActive && (!slotOf.TryGetValue(item, out int slot) || !active.Get(slot))) { uncovered++; }
        }
        return uncovered;
    }

    public static bool VerifyOrder()
    {
        int prev = -1;
        foreach (Item item in Item.ItemList)
        {
            if (!slotOf.TryGetValue(item, out int slot) || slot <= prev) { return false; }
            prev = slot;
        }
        return true;
    }
}
