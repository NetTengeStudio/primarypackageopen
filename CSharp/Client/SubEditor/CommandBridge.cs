using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// The seam between the original editor and the server. The original editor edits entities locally and then calls
/// SubEditorScreen.StoreCommand with a command describing the change. While the live editor is open this prefix turns
/// that command into a server request instead of a local undo entry. The local edit already happened (optimistic);
/// if the server refuses it, the rollback restores the previous state. Undo/Redo become server requests too, because
/// history lives in the authority (per GM, conflict-checked), not in the client.
/// </summary>
internal static class CommandBridge
{
    private static readonly System.Reflection.FieldInfo TransformReceivers = AccessTools.Field(typeof(TransformCommand), "Receivers");
    private static readonly System.Reflection.FieldInfo TransformNew = AccessTools.Field(typeof(TransformCommand), "NewData");
    private static readonly System.Reflection.FieldInfo PropertyReceivers = AccessTools.Field(typeof(PropertyCommand), "Receivers");
    private static readonly System.Reflection.FieldInfo PropertyName = AccessTools.Field(typeof(PropertyCommand), "PropertyName");
    private static readonly System.Reflection.FieldInfo PropertyNew = AccessTools.Field(typeof(PropertyCommand), "NewProperties");
    private static readonly System.Reflection.FieldInfo WasDeleted = AccessTools.Field(typeof(AddOrDeleteCommand), "WasDeleted");
    private sealed record DeletedState(ushort[] Ids, ushort Submarine, byte[] Xml);
    private static readonly ConditionalWeakTable<AddOrDeleteCommand, DeletedState> DeletedStates = new();

    public static void Attach(Harmony harmony)
    {
        harmony.Patch(AccessTools.Constructor(typeof(AddOrDeleteCommand), new[] { typeof(List<MapEntity>), typeof(bool), typeof(bool) }),
            prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(CommandConstructing)), finalizer: ModuleHost.Bind(typeof(CommandBridge), nameof(CommandConstructed)));
        ModuleHost.Guard("SubEditorScreen.StoreCommand", () => harmony.Patch(
            AccessTools.Method(typeof(SubEditorScreen), nameof(SubEditorScreen.StoreCommand)),
            prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(StorePrefix))));
        ModuleHost.Guard("SubEditorScreen.GenerateWaypoints", () => harmony.Patch(
            AccessTools.Method(typeof(SubEditorScreen), "GenerateWaypoints"), prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(GenerateWaypointsPrefix))));
        ModuleHost.Guard("SubEditorScreen.AutoHull", () => harmony.Patch(
            AccessTools.Method(typeof(SubEditorScreen), nameof(SubEditorScreen.AutoHull)),
            prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(AutoHullPrefix)), postfix: ModuleHost.Bind(typeof(CommandBridge), nameof(AutoHullPostfix)),
            finalizer: ModuleHost.Bind(typeof(CommandBridge), nameof(AutoHullFinalizer))));
        WireTracker.Attach(harmony);
        InventoryBridge.Attach(harmony);
        InventoryReplication.Attach(harmony);
        ModuleHost.Guard("Submarine.TrySaveAs", () => harmony.Patch(
            AccessTools.Method(typeof(Submarine), nameof(Submarine.TrySaveAs)), prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(SaveAsPrefix))));
        ModuleHost.Guard("Submarine.SaveToXElement", () => harmony.Patch(
            AccessTools.Method(typeof(Submarine), nameof(Submarine.SaveToXElement)),
            prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(SaveWorldPrefix)),
            finalizer: ModuleHost.Bind(typeof(CommandBridge), nameof(SaveWorldFinalizer))));
        ModuleHost.Guard("SubEditorScreen.LoadSub", () => harmony.Patch(
            AccessTools.Method(typeof(SubEditorScreen), nameof(SubEditorScreen.LoadSub)),
            prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(LoadSubPrefix))));
        ModuleHost.Guard("SubEditorScreen.CreateDummyCharacter", () => harmony.Patch(
            AccessTools.Method(typeof(SubEditorScreen), "CreateDummyCharacter"),
            postfix: ModuleHost.Bind(typeof(CommandBridge), nameof(DummyCreated))));
        ModuleHost.Guard("ServerSettings.AllowRewiring", () => harmony.Patch(
            AccessTools.PropertyGetter(typeof(ServerSettings), nameof(ServerSettings.AllowRewiring)),
            postfix: ModuleHost.Bind(typeof(CommandBridge), nameof(RewiringAllowed))));
        ModuleHost.Guard("Character.ClientWriteInput", () => harmony.Patch(
            AccessTools.Method(typeof(Character), nameof(Character.ClientWriteInput)), prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(DummyInputPrefix))));
        ModuleHost.Guard("Barotrauma.Networking.ClientEntityEventManager.CreateEvent", () => harmony.Patch(
            AccessTools.Method(typeof(Barotrauma.Networking.ClientEntityEventManager), nameof(Barotrauma.Networking.ClientEntityEventManager.CreateEvent)), prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(DummyEventPrefix))));
        ModuleHost.Guard("Character.Update", () => harmony.Patch(
            AccessTools.Method(typeof(Character), nameof(Character.Update), new[] { typeof(float), typeof(Camera) }), prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(DummyUpdatePrefix))));
        CircuitBridge.Attach(harmony);
        ModuleHost.Guard("SubEditorScreen.ClearUndoBuffer", () => harmony.Patch(
            AccessTools.Method(typeof(SubEditorScreen), "ClearUndoBuffer"), prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(KeepHistoryPrefix))));
        ModuleHost.Guard("SubEditorScreen.CreateLoadScreen", () => harmony.Patch(
            AccessTools.Method(typeof(SubEditorScreen), "CreateLoadScreen"), prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(LoadPrefix))));
        ModuleHost.Guard("SubEditorScreen.Undo", () => harmony.Patch(
            AccessTools.Method(typeof(SubEditorScreen), "Undo"), prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(UndoPrefix))));
        ModuleHost.Guard("SubEditorScreen.Redo", () => harmony.Patch(
            AccessTools.Method(typeof(SubEditorScreen), "Redo"), prefix: ModuleHost.Bind(typeof(CommandBridge), nameof(RedoPrefix))));
    }

    private static void CommandConstructing(AddOrDeleteCommand __instance, List<MapEntity> receivers, bool wasDeleted, out bool __state)
    {
        __state = false;
        if (!SubEditorClient.Editing || LiveEditorScreen.GameUpdating || WireTracker.Suppressed) { return; }
        if (wasDeleted)
        {
            List<MapEntity> all = EntityOps.WithContents(receivers.Select(entity => entity.ID).Distinct().OrderBy(id => id).ToArray());
            if (all.Count != 0) { DeletedStates.AddOrUpdate(__instance, new DeletedState(all.Select(entity => entity.ID).ToArray(), SubRegistry.WireId(all[0].Submarine), EntityOps.SaveXml(all))); }
        }
        WireTracker.Enter();
        __state = true;
    }

    private static void CommandConstructed(bool __state) { if (__state) { WireTracker.Exit(); } }

    /// <summary>
    /// The engine's save replaces the submarine's info with the new file's, which in a running round renames the live submarine.
    /// While the live editor is open the file is written from a copy instead and the live submarine is left alone.
    /// </summary>
    private static bool SaveAsPrefix(Submarine __instance, string filePath, System.IO.MemoryStream previewImage, ref bool __result)
    {
        if (!SubEditorClient.Editing || __instance != Submarine.MainSub) { return true; }
        __result = LiveSave.Write(__instance, filePath, previewImage);
        return false;
    }

    private static void SaveWorldPrefix(out Screen __state)
    {
        __state = Screen.Selected is LiveEditorScreen ? Screen.Selected : null;
        if (__state != null) { Screen.Selected = GameMain.GameScreen; }
    }

    private static void SaveWorldFinalizer(Screen __state)
    {
        if (__state != null) { Screen.Selected = __state; }
    }

    private static bool LoadSubPrefix(SubmarineInfo info)
    {
        if (Screen.Selected is not LiveEditorScreen screen) { return true; }
        screen.AddSubmarine(info);
        return false;
    }

    private static void DummyCreated(SubEditorScreen __instance)
    {
        if (__instance is not LiveEditorScreen || __instance.dummyCharacter is not Character dummy) { return; }
        dummy.AnimController.Frozen = true;
        dummy.AnimController.Collider.Enabled = false;
        foreach (Limb limb in dummy.AnimController.Limbs) { limb.body.Enabled = false; }
    }

    private static void RewiringAllowed(ref bool __result)
    {
        if (SubEditorClient.Editing && !LiveEditorScreen.GameUpdating) { __result = true; }
    }

    /// <summary>The editor's dummy character exists on this client only; the server must not read its keys as the player's own.</summary>
    private static bool DummyInputPrefix(Character __instance) => !(SubEditorClient.Editing && __instance.ID == Entity.DummyID);

    /// <summary>The editor updates its dummy itself; the round's update (which also runs while the editor is open) leaves it alone.</summary>
    private static bool DummyUpdatePrefix(Character __instance) => !(LiveEditorScreen.GameUpdating && __instance.ID == Entity.DummyID);

    /// <summary>
    /// While the editor is open every change to the world goes through its own operations. The engine's own entity events must stay home: putting an item
    /// into a container, dropping it or editing a component would otherwise also be sent to the server as a player's action, which it checks against a
    /// character that is not there, refuses, and answers with the old state, undoing the edit on this client.
    /// </summary>
    private static bool DummyEventPrefix() => !WireTracker.Suppressed && (!SubEditorClient.Editing || LiveEditorScreen.GameUpdating);

    /// <summary>
    /// Leaving the editor clears the original's history panel, but the server keeps this editor's history for the whole round (Ctrl+Z still works after F4).
    /// The panel therefore keeps its rows too; it is rebuilt only when the link restarts (SubEditorClient.Join).
    /// </summary>
    private static bool KeepHistoryPrefix()
    {
        if (Screen.Selected is not LiveEditorScreen || SubEditorClient.State != LinkState.Live) { return true; }
        SerializableEntityEditor.PropertyChangesActive = false;
        SerializableEntityEditor.CommandBuffer = null;
        return false;
    }

    /// <summary>Loading another submarine would replace the round's world; adding one to the round is a separate server operation.</summary>
    private static bool LoadPrefix() => Screen.Selected is not LiveEditorScreen;

    private static bool UndoPrefix(int amount) { if (!SubEditorClient.Editing) { return true; } for (int i = 0; i < amount; i++) { SubEditorClient.SendHistory(true); } return false; }

    private static bool RedoPrefix(int amount) { if (!SubEditorClient.Editing) { return true; } for (int i = 0; i < amount; i++) { SubEditorClient.SendHistory(false); } return false; }

    /// <returns>false to skip the original (command handled by the server path).</returns>
    private static bool StorePrefix(Command command)
    {
        if (!SubEditorClient.Editing) { return true; }
        if (Capturing || WireTracker.Suppressed) { WireTracker.Quietly(command.Cleanup); return false; }
        try
        {
            switch (command)
            {
                case TransformToolCommand tool when SendToolState(tool): return false;
                case InventoryPlaceCommand or InventoryMoveCommand when SendInventory(command): return false;
                case TransformCommand t when SendTransform(t): return false;
                case PropertyCommand p when SendProperty(p): return false;
                case AddOrDeleteCommand a when SendAddOrDelete(a): return false;
            }
        }
        catch (Exception e) when (e is InvalidCastException or NullReferenceException or ArgumentException)
        {
            ModLog.Once($"subeditor: {command.GetType().Name} could not be sent: {e.Message}");
        }
        // Anything the live editor cannot replicate yet is undone locally rather than left as a silent divergence.
        try { command.UnExecute(); } catch (Exception e) { ModLog.Once($"subeditor: {command.GetType().Name} revert failed: {e.Message}"); }
        global::Barotrauma.GUI.AddMessage(SubEditorClient.State == LinkState.Live
            ? $"Действие «{command.GetType().Name.Replace("Command", "")}» в сетевом редакторе пока не поддерживается."
            : "Редактор ещё не подключился к серверу.", Color.Orange);
        return false;
    }

    private static bool SendTransform(TransformCommand command)
    {
        var receivers = (List<MapEntity>)TransformReceivers.GetValue(command);
        var rects = (List<Rectangle>)TransformNew.GetValue(command);
        if (receivers.Count != rects.Count || receivers.Count == 0) { return false; }
        var order = Enumerable.Range(0, receivers.Count).OrderBy(i => receivers[i].ID).ToArray();
        ushort[] ids = order.Select(i => receivers[i].ID).ToArray();
        if (ids.Distinct().Count() != ids.Length) { return false; }
        var records = new TransformRecord[order.Length];
        for (int k = 0; k < order.Length; k++)
        {
            int i = order[k];
            Rectangle rect = EntityOps.LocalRect(rects[i], receivers[i].Submarine);
            records[k] = new TransformRecord { Fields = TransformFields.Position | TransformFields.Size | TransformFields.Frame, X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height, SubmarineId = SubRegistry.WireId(receivers[i].Submarine) };
        }
        var w = new ByteWriter(64);
        try
        {
            TransformPayload.Write(ref w, records);
            return SubEditorClient.SendRequest(OpKind.Transform, ids, w.Written, () => WireTracker.Quietly(command.UnExecute), description: command.GetDescription().Value);
        }
        finally { w.Dispose(); }
    }

    private static bool SendProperty(PropertyCommand command)
    {
        var receivers = (List<ISerializableEntity>)PropertyReceivers.GetValue(command);
        var name = (Identifier)PropertyName.GetValue(command);
        string value = EntityOps.Stringify(PropertyNew.GetValue(command));
        var rows = new List<(ushort Id, short Component)>();
        foreach (ISerializableEntity r in receivers)
        {
            switch (r)
            {
                case MapEntity me: rows.Add((me.ID, -1)); break;
                case ItemComponent ic: rows.Add((ic.Item.ID, (short)ic.Item.Components.IndexOf(ic))); break;
                default: return false;
            }
        }
        rows.Sort((a, b) => a.Id.CompareTo(b.Id));
        if (rows.Count == 0 || rows.Select(r => r.Id).Distinct().Count() != rows.Count || rows.Any(r => r.Component < -1)) { return false; }
        ushort[] ids = rows.Select(r => r.Id).ToArray();
        short[] comps = rows.Select(r => r.Component).ToArray();
        string[] values = Enumerable.Repeat(value, rows.Count).ToArray();
        var w = new ByteWriter(64);
        try
        {
            PropertyPayload.Write(ref w, name.Value, comps, values);
            return SubEditorClient.SendRequest(OpKind.Property, ids, w.Written, () => WireTracker.Quietly(command.UnExecute), description: command.GetDescription().Value);
        }
        finally { w.Dispose(); }
    }

    private static bool SendAddOrDelete(AddOrDeleteCommand command)
    {
        bool deleted = (bool)WasDeleted.GetValue(command);
        List<MapEntity> receivers = command.Receivers.Where(e => e != null).ToList();
        if (receivers.Count == 0) { return false; }

        if (deleted)
        {
            if (!DeletedStates.TryGetValue(command, out DeletedState snapshot)) { return false; }
            InventoryBridge.QueueDelete(snapshot.Ids, snapshot.Submarine, snapshot.Xml);
            InventoryBridge.Forget(EntityOps.WithContents(snapshot.Ids).Concat(receivers));
            WireTracker.Quietly(command.Cleanup);
            return true;
        }

        List<MapEntity> alive = receivers.Where(e => !e.Removed).ToList();
        if (alive.Count == 0) { return false; }
        ushort[] aliveIds = alive.Select(e => e.ID).Distinct().OrderBy(i => i).ToArray();
        List<MapEntity> all = EntityOps.WithContents(aliveIds);
        if (all.Count == 0) { return false; }
        ushort subWire = SubRegistry.WireId(all[0].Submarine);
        if ((GameMain.SubEditorScreen as LiveEditorScreen)?.SelectedLayer is string layer)
        {
            // The original assigns the layer picked in the layer panel to everything placed; its store step is skipped here, so it happens now.
            WireTracker.Quietly(() => { foreach (MapEntity e in all) { e.Layer = layer; } });
        }
        byte[] xml = EntityOps.SaveXml(all);
        var w = new ByteWriter(xml.Length / 2 + 32);
        try
        {
            SpawnPayload.Write(ref w, subWire, xml);
            MapEntity[] clones = all.ToArray();
            Action dropClones = () => WireTracker.Quietly(() => { for (int i = clones.Length - 1; i >= 0; i--) { if (!clones[i].Removed) { clones[i].Remove(); } } });
            InventoryBridge.Forget(clones);
            bool sent = SubEditorClient.SendRequest(OpKind.Spawn, ReadOnlySpan<ushort>.Empty, w.Written, dropClones, description: command.GetDescription().Value);
            dropClones();
            if (!sent) { global::Barotrauma.GUI.AddMessage("Не удалось отправить размещение предметов на сервер.", Color.Orange); }
            MapEntity.DeselectAll();
            WireTracker.Quietly(command.Cleanup);
            return true;
        }
        finally { w.Dispose(); }
    }

    private static void Respawn(ushort sub, byte[] xml)
    {
        Submarine s = sub == 0 ? Submarine.MainSub : Submarine.Loaded.FirstOrDefault(x => x.ID == sub);
        if (s != null) { EntityOps.LoadExact(xml, s); }
    }

    // ---- tool, inventory, generators ---------------------------------------------------------------------------

    private static readonly System.Reflection.FieldInfo ToolData = AccessTools.Field(typeof(TransformToolCommand), "originalData");
    private static readonly System.Reflection.FieldInfo PlaceItems = AccessTools.Field(typeof(InventoryPlaceCommand), "Receivers");

    /// <summary>Set while a generator (auto-hull) runs locally: the entities it creates must not each become a request.</summary>
    public static bool Capturing;

    /// <summary>
    /// Rotate/scale widget: the result is "these entities now look like this", so it travels as entity states.
    /// Wires attached to the transformed entities move with them and are included.
    /// </summary>
    private static bool SendToolState(TransformToolCommand command)
    {
        var data = (Dictionary<MapEntity, SubEditorScreen.TransformData>)ToolData.GetValue(command);
        var ids = new SortedSet<ushort>();
        foreach (var pair in data)
        {
            ids.Add(pair.Key.ID);
            if (pair.Value.Wires != null) { foreach (Wire wire in pair.Value.Wires.Keys) { ids.Add(wire.Item.ID); } }
        }
        ushort[] sorted = ids.ToArray();
        var blobs = new byte[sorted.Length][];
        if (sorted.Length == 0 || !EntityOps.CaptureStates(sorted, blobs)) { return false; }
        var w = new ByteWriter(512);
        try
        {
            StatePayload.Write(ref w, blobs);
            return SubEditorClient.SendRequest(OpKind.State, sorted, w.Written, () => WireTracker.Quietly(command.UnExecute), description: command.GetDescription().Value);
        }
        finally { w.Dispose(); }
    }

    private static bool SendInventory(Command command)
    {
        if (command is InventoryPlaceCommand dropPlace && Screen.Selected is SubEditorScreen editor && (editor.EntityMenu?.Rect.Contains(PlayerInput.MousePosition) == true || editor.WiringMode))
        {
            var slotItems = (List<InventorySlotItem>)PlaceItems.GetValue(dropPlace);
            InventoryBridge.Delete(slotItems);
        }
        return true;
    }

    private static bool GenerateWaypointsPrefix(ref bool __result)
    {
        if (!SubEditorClient.Editing) { return true; }
        __result = SubEditorClient.SendRequest(OpKind.Waypoints, ReadOnlySpan<ushort>.Empty, ReadOnlySpan<byte>.Empty, null);
        return false;
    }

    private sealed class HullState
    {
        public HashSet<ushort> Before;
        public byte[] BeforeXml;
        public ushort Sub;
        public EditorWorldScope Scope;
    }

    private static IEnumerable<MapEntity> HullsAndGaps() => MapEntity.MapEntityList.Where(e => (e is Hull || e is Gap) && !e.Removed && e.Submarine == Submarine.MainSub);

    private static void AutoHullPrefix(out HullState __state)
    {
        __state = null;
        if (!SubEditorClient.Editing) { return; }
        List<MapEntity> before = HullsAndGaps().ToList();
        __state = new HullState { Before = before.Select(e => e.ID).ToHashSet(), BeforeXml = EntityOps.SaveXml(before), Sub = SubRegistry.WireId(Submarine.MainSub) };
        __state.Scope = new EditorWorldScope(Submarine.MainSub);
        Capturing = true;
    }

    private static void AutoHullFinalizer(HullState __state, Exception __exception)
    {
        if (__state == null) { return; }
        Capturing = false;
        __state.Scope.Dispose();
        if (__exception == null) { return; }
        WireTracker.Quietly(() =>
        {
            EntityOps.Delete(HullsAndGaps().Select(e => e.ID).OrderBy(id => id).ToArray());
            Respawn(__state.Sub, __state.BeforeXml);
        });
    }

    /// <summary>
    /// The auto-hull algorithm lives in the editor UI class, so it runs on the client; its outcome is sent as one batch:
    /// delete the old hulls and gaps, create the new ones. The local copies are dropped, the server's replace them.
    /// </summary>
    private static void AutoHullPostfix(HullState __state)
    {
        if (__state == null) { return; }
        Capturing = false;
        __state.Scope.Dispose();
        List<MapEntity> after = HullsAndGaps().ToList();
        ushort[] created = after.Where(e => !__state.Before.Contains(e.ID)).Select(e => e.ID).OrderBy(i => i).ToArray();
        ushort[] removed = __state.Before.Where(id => EntityOps.Find(id) == null).OrderBy(i => i).ToArray();
        byte[] createdXml = EntityOps.SaveXml(EntityOps.WithContents(created));
        ushort sub = __state.Sub;
        var subs = new List<BatchPayload.Sub>();
        if (removed.Length > 0) { subs.Add(new BatchPayload.Sub(OpKind.Delete, removed, Array.Empty<byte>())); }
        if (created.Length > 0)
        {
            var sp = new ByteWriter(createdXml.Length / 2 + 32); SpawnPayload.Write(ref sp, sub, createdXml);
            subs.Add(new BatchPayload.Sub(OpKind.Spawn, created, sp.Written.ToArray())); sp.Dispose();
        }
        if (subs.Count == 0) { return; }
        var w = new ByteWriter(createdXml.Length / 2 + 64);
        try
        {
            BatchPayload.Write(ref w, subs.ToArray());
            bool sent = SubEditorClient.SendRequest(OpKind.Batch, ReadOnlySpan<ushort>.Empty, w.Written, rollback: null, description: "Auto hull");
            WireTracker.Quietly(() =>
            {
                EntityOps.Delete(created);
                var missing = System.Xml.Linq.XElement.Parse(System.Text.Encoding.UTF8.GetString(__state.BeforeXml));
                foreach (var element in missing.Elements().ToArray())
                {
                    if (ushort.TryParse((string)element.Attribute("ID"), out ushort id) && EntityOps.Find(id) != null) { element.Remove(); }
                }
                if (missing.HasElements && EntityOps.LoadExact(System.Text.Encoding.UTF8.GetBytes(missing.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)), SubRegistry.Find(sub)) == null)
                { ModLog.Error("subeditor: autohull preview could not be restored"); }
            });
            if (!sent) { global::Barotrauma.GUI.AddMessage("Не удалось отправить AutoHull на сервер.", Color.Orange); }
        }
        finally { w.Dispose(); }
    }
}
