using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using IEngine.Core;

namespace IEngine.Execution;

static class ItemRegistry
{
    const int SweepPerTick = 96;

    static readonly int[] itemSlotById = new int[65536];
    static Item[] items = new Item[8192];
    static readonly SlotBitSet active = new();
    static int nextSlot, live, sweepCursor;

    static ItemRegistry()
    {
        Array.Fill(itemSlotById, -1);
    }

    public static bool Tracking;
    public static long Rebuilds, Missed, Compactions;

    public static SlotBitSet Active => active;
    public static Item[] Items => items;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreIsActive(Item item, bool value)
    {
        bool was = item.IsActive;
        item.IsActive = value;
        if (!Tracking || (!value && Simulation.Dormancy.Probing == null) || !TryGetSlot(item, out int slot)) { return; }
        if (value) { active.Set(slot); }
        Simulation.Dormancy.OnIsActiveWritten(slot, was != value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetSlot(Item item, out int slot)
    {
        if (item != null)
        {
            int s = itemSlotById[item.ID];
            if ((uint)s < (uint)nextSlot && ReferenceEquals(items[s], item))
            {
                slot = s;
                return true;
            }
        }
        slot = -1;
        return false;
    }

    public static void OnListed(Item item)
    {
        if (!Tracking || TryGetSlot(item, out _)) { return; }
        Assign(item);
    }

    public static void OnUnlisted(Item item)
    {
        if (!Tracking || !TryGetSlot(item, out int slot)) { return; }
        itemSlotById[item.ID] = -1;
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
        itemSlotById[item.ID] = slot;
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
        Array.Fill(itemSlotById, -1);
        Array.Clear(items, 0, nextSlot);
        active.ClearAll();
        nextSlot = live = 0;
    }

    public static void Rebuild()
    {
        Simulation.Dormancy.Reset();
        Simulation.SpatialCore.MarkDirty();
        HullIndex.Clear();
        Array.Fill(itemSlotById, -1);
        Array.Clear(items, 0, nextSlot);
        active.ClearAll();
        nextSlot = live = 0;
        sweepCursor = 0;
        foreach (Item item in Item.ItemList) { if (!TryGetSlot(item, out _)) { Assign(item); } }
        Rebuilds++;
    }

    static void Reconcile()
    {
        var list = Item.ItemList;
        if (live < list.Count)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Item item = list[i];
                if (!TryGetSlot(item, out _)) { Assign(item); }
            }
        }
        if (live > list.Count)
        {
            for (int s = 0; s < nextSlot; s++)
            {
                Item it = items[s];
                if (it != null && (it.Removed || itemSlotById[it.ID] != s))
                {
                    OnUnlisted(it);
                }
            }
        }
        if (live != list.Count) { Rebuild(); }
    }

    public static void BeginTick()
    {
        var list = Item.ItemList;
        if (live != list.Count) { Reconcile(); return; }
        if (nextSlot > 4096 && nextSlot > live * 2) { Rebuild(); Compactions++; return; }
        HullIndex.Sweep();
        int n = Math.Min(SweepPerTick, list.Count);
        for (int i = 0; i < n; i++)
        {
            if (sweepCursor >= list.Count) { sweepCursor = 0; }
            Item item = list[sweepCursor++];
            if (item.IsActive && TryGetSlot(item, out int slot) && !active.Get(slot))
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
            if (item.IsActive && (!TryGetSlot(item, out int slot) || !active.Get(slot))) { uncovered++; }
        }
        return uncovered;
    }

    public static bool VerifyOrder()
    {
        int prev = -1;
        foreach (Item item in Item.ItemList)
        {
            if (!TryGetSlot(item, out int slot) || slot <= prev) { return false; }
            prev = slot;
        }
        return true;
    }
}
