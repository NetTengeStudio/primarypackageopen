using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

internal static class WireTracker
{
    private static readonly HashSet<ushort> Dirty = new();
    private static readonly Dictionary<ushort, WireState> Known = new();
    private static readonly HashSet<Item> Local = new();
    private static readonly List<Item> Finished = new();
    private static readonly List<WireState> Current = new(), Previous = new();
    private static long lastMark;
    private static int quiet;

    public static bool Suppressed => quiet != 0;
    private static bool Tracking => !Suppressed && SubEditorClient.Editing && !LiveEditorScreen.GameUpdating;
    public static void Enter() => quiet++;
    public static void Exit() => quiet--;

    public static void Quietly(Action action)
    {
        Enter();
        try { action(); }
        finally { Exit(); }
    }

    public static void Attach(Harmony harmony)
    {
        ModuleHost.Guard("SubEditorScreen.SelectWire", () => harmony.Patch(
            AccessTools.Method(typeof(SubEditorScreen), "SelectWire"), postfix: ModuleHost.Bind(typeof(WireTracker), nameof(Selected))));
        foreach (System.Reflection.MethodInfo method in new[]
        {
            AccessTools.Method(typeof(Wire), "UpdateSections"),
            AccessTools.Method(typeof(Wire), nameof(Wire.SetNodes)),
            AccessTools.Method(typeof(Wire), nameof(Wire.Connect)),
            AccessTools.Method(typeof(Wire), nameof(Wire.RemoveConnection), new[] { typeof(Connection) }),
            AccessTools.Method(typeof(Wire), nameof(Wire.RemoveConnection), new[] { typeof(Item) }),
            AccessTools.Method(typeof(Wire), nameof(Wire.ClearConnections))
        })
        {
            if (method == null) { throw new MissingMethodException(typeof(Wire).FullName, "wire edit mutation"); }
            harmony.Patch(method, prefix: ModuleHost.Bind(typeof(WireTracker), nameof(Before)), postfix: ModuleHost.Bind(typeof(WireTracker), nameof(After)));
        }
    }

    private static void Selected(bool __result, object userData)
    {
        if (!__result || !Tracking || userData is not ItemPrefab prefab || Character.Controlled is not Character dummy) { return; }
        foreach (Item item in dummy.HeldItems)
        {
            if (item.Prefab == prefab && item.GetComponent<Wire>() != null) { Local.Add(item); }
        }
    }

    private static void Before(Wire __instance)
    {
        if (!Tracking || __instance?.Item is not Item item || Local.Contains(item) || Known.ContainsKey(item.ID)) { return; }
        if (EntityOps.CaptureWire(item.ID, out WireState before)) { Known.Add(item.ID, before); }
    }

    private static void After(Wire __instance)
    {
        if (!Tracking || __instance?.Item is not Item item || item.Removed) { return; }
        Dirty.Add(item.ID);
        lastMark = Environment.TickCount64;
    }

    private static bool SendNew(Item item)
    {
        Wire wire = item.GetComponent<Wire>();
        if (wire == null || item.ParentInventory is CharacterInventory || !EntityOps.CaptureWire(item.ID, out WireState state)) { return false; }
        if (state.End0.ItemId == 0 && state.End1.ItemId == 0) { return false; }
        Item end = EntityOps.Find(state.End0.ItemId != 0 ? state.End0.ItemId : state.End1.ItemId) as Item;
        if (end == null) { return false; }
        var w = new ByteWriter(256);
        try
        {
            WireNewPayload.Write(ref w, SubRegistry.WireId(end.Submarine), EntityOps.SaveXml(new MapEntity[] { item }), state);
            Action remove = () => Quietly(() => { if (!item.Removed) { item.Remove(); } });
            return SubEditorClient.SendRequest(OpKind.WireNew, ReadOnlySpan<ushort>.Empty, w.Written, remove, remove, replaceLocal: true);
        }
        finally { w.Dispose(); }
    }

    public static void Remember(WireState state) => Known[state.WireId] = state;

    public static void Reset()
    {
        Dirty.Clear(); Known.Clear(); Local.Clear(); Finished.Clear(); Current.Clear(); Previous.Clear();
    }

    private static bool Same(WireState a, WireState b) => a.End0 == b.End0 && a.End1 == b.End1 && a.Nodes.AsSpan().SequenceEqual(b.Nodes);

    public static void Flush()
    {
        if (Suppressed || !SubEditorClient.Editing || PlayerInput.PrimaryMouseButtonHeld()) { return; }
        if (Dirty.Count == 0 && Local.Count == 0 || Environment.TickCount64 - lastMark < 150) { return; }
        Finished.Clear();
        foreach (Item item in Local)
        {
            if (item.Removed || SendNew(item)) { Finished.Add(item); Dirty.Remove(item.ID); Known.Remove(item.ID); }
        }
        foreach (Item item in Finished) { Local.Remove(item); }
        Finished.Clear();
        if (Dirty.Count == 0) { return; }
        Current.Clear(); Previous.Clear();
        foreach (ushort id in Dirty)
        {
            if (EntityOps.Find(id) is not Item item || Local.Contains(item) || !EntityOps.CaptureWire(id, out WireState state)) { continue; }
            if (!Known.TryGetValue(id, out WireState before) || Same(state, before)) { continue; }
            Current.Add(state); Previous.Add(before);
        }
        Dirty.Clear();
        if (Current.Count == 0) { return; }
        Current.Sort(static (a, b) => a.WireId.CompareTo(b.WireId));
        Previous.Sort(static (a, b) => a.WireId.CompareTo(b.WireId));
        var w = new ByteWriter(128);
        try
        {
            WireState[] states = Current.ToArray(), restore = Previous.ToArray();
            WirePayload.Write(ref w, states);
            Action rollback = () => Quietly(() =>
            {
                if (!EntityOps.ApplyWires(restore)) { ModLog.Error("subeditor: wire rollback failed"); }
                foreach (WireState state in restore) { Remember(state); }
            });
            if (SubEditorClient.SendRequest(OpKind.Wire, ReadOnlySpan<ushort>.Empty, w.Written, rollback))
            {
                foreach (WireState state in states) { Remember(state); }
            }
            else { rollback(); }
        }
        finally { Current.Clear(); Previous.Clear(); w.Dispose(); }
    }
}
