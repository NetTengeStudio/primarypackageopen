using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;

namespace PrimaryPackage.SubEditor;

internal static class InventoryReplication
{
    private sealed class State
    {
        public bool Fenced, Received;
        public long Fence, Latest;
        public List<ushort>[] Snapshot, Discard;
    }
    private static readonly ConditionalWeakTable<Inventory, State> States = new();
    private static bool clockStarted;
    private static ushort lastNative;
    private static long nativeClock;

    public static void Tick()
    {
        if (GameMain.Client?.EntityEventManager == null) { return; }
        if (GameMain.Client.MidRoundSyncing) { return; }
        ushort current = GameMain.Client.EntityEventManager.LastReceivedID;
        if (!clockStarted) { nativeClock = current; lastNative = current; clockStarted = true; return; }
        int advance = unchecked((short)(current - lastNative));
        if (advance > 0) { nativeClock += advance; lastNative = current; }
    }

    private static long Resolve(ushort sequence)
    {
        Tick();
        if (!clockStarted) { nativeClock = sequence; lastNative = sequence; clockStarted = true; }
        return nativeClock + unchecked((short)(sequence - lastNative));
    }

    public static void Attach(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Inventory), nameof(Inventory.ClientEventRead)),
            prefix: new HarmonyMethod(typeof(InventoryReplication), nameof(Receiving)), postfix: new HarmonyMethod(typeof(InventoryReplication), nameof(Remember)));
        harmony.Patch(AccessTools.Method(typeof(Inventory), nameof(Inventory.ApplyReceivedState)), prefix: new HarmonyMethod(typeof(InventoryReplication), nameof(Remember)));
    }

    private static State Get(Inventory inventory) => States.GetValue(inventory, static _ => new State());

    private static bool Receiving(Inventory __instance, IReadMessage msg)
    {
        State state = Get(__instance);
        int position = msg.BitPosition;
        ushort sequence = msg.ReadUInt16();
        long stamp = Resolve(sequence);
        if (state.Fenced && stamp <= state.Fence)
        {
            state.Discard ??= new List<ushort>[__instance.Capacity];
            __instance.SharedRead(msg, state.Discard, out _);
            Array.Clear(state.Discard);
            return false;
        }
        state.Latest = stamp; state.Received = true;
        msg.BitPosition = position;
        return true;
    }

    private static List<ushort>[] Clone(List<ushort>[] source)
    {
        if (source == null) { return null; }
        var copy = new List<ushort>[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != null) { copy[i] = new List<ushort>(source[i]); }
        }
        return copy;
    }

    private static void Remember(Inventory __instance)
    {
        if (__instance.receivedItemIDs != null) { Get(__instance).Snapshot = Clone(__instance.receivedItemIDs); }
    }

    public static void Fence(ReadOnlySpan<ushort> ids, ushort sequence)
    {
        foreach (ushort id in ids)
        {
            if (EntityOps.Find(id) is not Item item) { continue; }
            foreach (ItemInventory inventory in item.OwnInventories) { Fence(inventory, sequence); }
        }
    }

    internal static void Fence(Inventory inventory, ushort sequence)
    {
        State state = Get(inventory);
        long stamp = Resolve(sequence);
        if (!state.Fenced || stamp > state.Fence) { state.Fence = stamp; state.Fenced = true; }
        Remember(inventory);
        if (inventory.syncItemsCoroutine != null) { CoroutineManager.StopCoroutines(inventory.syncItemsCoroutine); inventory.syncItemsCoroutine = null; }
        inventory.syncItemsDelay = 0;
        inventory.receivedItemIDs = null;
        inventory.partialReceivedItemIDs = null;
        if (!state.Received || state.Latest <= state.Fence) { state.Snapshot = null; }
    }

    public static void Reconcile(ReadOnlySpan<ushort> ids, ushort sequence)
    {
        List<(Inventory Inventory, State State)> newer = null;
        foreach (ushort id in ids)
        {
            if (EntityOps.Find(id) is not Item item) { continue; }
            foreach (ItemInventory inventory in item.OwnInventories)
            {
                State state = Get(inventory);
                if (!state.Fenced) { Fence(inventory, sequence); }
                if (state.Snapshot != null && state.Received && state.Latest > state.Fence) { (newer ??= new()).Add((inventory, state)); }
            }
        }
        if (newer == null) { return; }
        newer.Sort(static (a, b) => a.State.Latest.CompareTo(b.State.Latest));
        foreach (var pair in newer)
        {
            pair.Inventory.receivedItemIDs = Clone(pair.State.Snapshot);
            pair.Inventory.ApplyReceivedState();
        }
    }

    public static void Reset() { States.Clear(); clockStarted = false; lastNative = 0; nativeClock = 0; }
}
