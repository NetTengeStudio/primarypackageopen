using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// Edits the original editor makes without a Command (so StoreCommand never sees them) are caught where they happen
/// and sent as ordinary operations: layer assignment, linking with Space+click, mirroring, submarine metadata.
/// All of them apply optimistically and carry a rollback, like the command-based edits.
/// </summary>
internal static class LayerTracker
{
    private static readonly Dictionary<ushort, (string Old, string New)> Pending = new();

    /// <summary>Layer names changed (here or by another editor): the editor's layer list must be rebuilt.</summary>
    public static bool NamesChanged;

    public static void Attach(Harmony harmony)
    {
        ModuleHost.Guard("MapEntity.Layer", () => harmony.Patch(
            AccessTools.PropertySetter(typeof(MapEntity), nameof(MapEntity.Layer)),
            prefix: ModuleHost.Bind(typeof(LayerTracker), nameof(Before)), postfix: ModuleHost.Bind(typeof(LayerTracker), nameof(After))));
    }

    private static void Before(MapEntity __instance, out string __state) => __state = __instance.Layer;

    private static void After(MapEntity __instance, string __state)
    {
        if (!SubEditorClient.Editing || WireTracker.Suppressed || __instance == null) { return; }
        string now = __instance.Layer ?? "", before = __state ?? "";
        if (before == now) { return; }
        ushort id = __instance.ID;
        Pending[id] = Pending.TryGetValue(id, out var first) ? (first.Old, now) : (before, now);
        NamesChanged = true;
    }

    public static void Reset() { Pending.Clear(); NamesChanged = false; }

    public static void Flush()
    {
        if (Pending.Count == 0) { return; }
        var rows = Pending.Where(p => p.Value.Old != p.Value.New && EntityOps.Find(p.Key) != null).OrderBy(p => p.Key).ToArray();
        Pending.Clear();
        if (rows.Length == 0) { return; }
        ushort[] ids = rows.Select(r => r.Key).ToArray();
        string[] values = rows.Select(r => r.Value.New).ToArray();
        string[] olds = rows.Select(r => r.Value.Old).ToArray();
        short[] comps = new short[ids.Length];
        Array.Fill(comps, (short)-1);
        var w = new ByteWriter(64 + ids.Length * 8);
        try
        {
            PropertyPayload.Write(ref w, nameof(MapEntity.Layer), comps, values);
            SubEditorClient.SendRequest(OpKind.Property, ids, w.Written, () =>
            {
                WireTracker.Quietly(() => { for (int i = 0; i < ids.Length; i++) { if (EntityOps.Find(ids[i]) is MapEntity e) { e.Layer = olds[i]; } } });
                NamesChanged = true;
            });
        }
        finally { w.Dispose(); }
    }
}

internal static class LinkTracker
{
    public static void Attach(Harmony harmony)
    {
        foreach (Type type in new[] { typeof(Item), typeof(Hull), typeof(WayPoint), typeof(LinkedSubmarine) })
        {
            System.Reflection.MethodInfo target = AccessTools.DeclaredMethod(type, "UpdateEditing", new[] { typeof(Camera), typeof(float) });
            if (target == null) { continue; }
            ModuleHost.Guard($"{type.Name}.UpdateEditing", () => harmony.Patch(target,
                prefix: ModuleHost.Bind(typeof(LinkTracker), nameof(Before)), postfix: ModuleHost.Bind(typeof(LinkTracker), nameof(After))));
        }
    }

    private static ushort[] LinkedIds(MapEntity e) => e.linkedTo.Where(l => !l.Removed).Select(l => l.ID).OrderBy(i => i).ToArray();

    /// <summary>Links only change on Space+click, so nothing is captured on any other frame.</summary>
    private static void Before(MapEntity __instance, out ushort[] __state)
    {
        __state = SubEditorClient.Editing && PlayerInput.KeyDown(Keys.Space) && __instance.linkedTo != null ? LinkedIds(__instance) : null;
    }

    private static void After(MapEntity __instance, ushort[] __state)
    {
        if (__state == null || WireTracker.Suppressed) { return; }
        ushort[] now = LinkedIds(__instance);
        var pairs = new List<(ushort A, ushort B, bool Linked)>();
        foreach (ushort id in now.Except(__state)) { pairs.Add((__instance.ID, id, true)); }
        foreach (ushort id in __state.Except(now)) { pairs.Add((__instance.ID, id, false)); }
        if (pairs.Count == 0) { return; }
        ushort[] ids = pairs.SelectMany(p => new[] { p.A, p.B }).Distinct().OrderBy(i => i).ToArray();
        var w = new ByteWriter(64);
        try
        {
            LinkPayload.Write(ref w, pairs.ToArray());
            var undo = pairs.ToArray();
            SubEditorClient.SendRequest(OpKind.Link, ids, w.Written, () =>
            {
                WireTracker.Quietly(() => { foreach (var (a, b, linked) in undo) { EntityOps.SetLink(a, b, !linked); } });
            });
        }
        finally { w.Dispose(); }
    }
}

internal static class FlipTracker
{
    private sealed class Capture
    {
        public ushort[] Ids;
        public FlipRecord[] Before;
    }

    public static void Attach(Harmony harmony)
    {
        ModuleHost.Guard("MapEntity.UpdateEditor", () => harmony.Patch(
            AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateEditor), new[] { typeof(Camera), typeof(float) }),
            prefix: ModuleHost.Bind(typeof(FlipTracker), nameof(Before)), postfix: ModuleHost.Bind(typeof(FlipTracker), nameof(After))));
    }

    /// <summary>Ctrl+N / Ctrl+M mirror the selection; the orientation is read before and after the original handles the key.</summary>
    private static void Before(out Capture __state)
    {
        __state = null;
        if (!SubEditorClient.Editing || MapEntity.SelectedList.Count == 0 || !PlayerInput.IsCtrlDown() || !(PlayerInput.KeyHit(Keys.N) || PlayerInput.KeyHit(Keys.M))) { return; }
        ushort[] ids = MapEntity.SelectedList.Where(e => !e.Removed).Select(e => e.ID).Distinct().OrderBy(i => i).ToArray();
        var before = new FlipRecord[ids.Length];
        if (ids.Length > 0 && EntityOps.CaptureFlip(ids, before)) { __state = new Capture { Ids = ids, Before = before }; }
    }

    private static void After(Capture __state)
    {
        if (__state == null) { return; }
        var after = new FlipRecord[__state.Ids.Length];
        if (!EntityOps.CaptureFlip(__state.Ids, after)) { return; }
        var ids = new List<ushort>(); var now = new List<FlipRecord>(); var old = new List<FlipRecord>();
        for (int i = 0; i < after.Length; i++)
        {
            if (after[i] == __state.Before[i]) { continue; }
            ids.Add(__state.Ids[i]); now.Add(after[i]); old.Add(__state.Before[i]);
        }
        if (ids.Count == 0) { return; }
        ushort[] idArray = ids.ToArray(); FlipRecord[] oldArray = old.ToArray();
        var w = new ByteWriter(16 + ids.Count * 12);
        try
        {
            FlipPayload.Write(ref w, now.ToArray());
            SubEditorClient.SendRequest(OpKind.Flip, idArray, w.Written, () => WireTracker.Quietly(() => EntityOps.ApplyFlip(idArray, oldArray)));
        }
        finally { w.Dispose(); }
    }
}
