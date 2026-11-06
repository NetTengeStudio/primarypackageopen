using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

internal static class InventoryBridge
{
    private readonly record struct Deletion(ushort[] Ids, ushort Submarine, byte[] Xml);
    private static readonly Dictionary<Item, InventoryPlacement> Before = new();
    private static readonly HashSet<Item> Local = new();
    private static readonly List<Deletion> Deleted = new();
    private static readonly List<Item> Changed = new();
    private static int lastOpenFrame = -1;
    private static Vector2 oldHoldablePosition;
    private static bool Tracking => LiveEditorScreen.EditorUpdating && SubEditorClient.Editing && !WireTracker.Suppressed;

    public static void Attach(Harmony harmony)
    {
        harmony.Patch(AccessTools.Constructor(typeof(Item), new[] { typeof(Rectangle), typeof(ItemPrefab), typeof(Submarine), typeof(bool), typeof(ushort) }),
            postfix: new HarmonyMethod(typeof(InventoryBridge), nameof(Created)));
        harmony.Patch(AccessTools.Method(typeof(Inventory), nameof(Inventory.TryPutItem), new[]
        {
            typeof(Item), typeof(int), typeof(bool), typeof(bool), typeof(Character), typeof(bool), typeof(bool), typeof(bool)
        }), prefix: new HarmonyMethod(typeof(InventoryBridge), nameof(Putting)));
        harmony.Patch(AccessTools.Method(typeof(Inventory), nameof(Inventory.RemoveItem)), prefix: new HarmonyMethod(typeof(InventoryBridge), nameof(Removing)));
        harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.ShallowRemove)), postfix: new HarmonyMethod(typeof(InventoryBridge), nameof(ShallowRemoved)));
        harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Remove)), postfix: new HarmonyMethod(typeof(InventoryBridge), nameof(ShallowRemoved)));
        harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Drop)), prefix: new HarmonyMethod(typeof(InventoryBridge), nameof(Dropping)),
            postfix: new HarmonyMethod(typeof(InventoryBridge), nameof(Dropped)));
        harmony.Patch(AccessTools.Method(typeof(SubEditorScreen), "OpenItem"), prefix: new HarmonyMethod(typeof(InventoryBridge), nameof(Open)));
        harmony.Patch(AccessTools.Method(typeof(SubEditorScreen), "CloseItem"), prefix: new HarmonyMethod(typeof(InventoryBridge), nameof(Close)));
    }

    private static void Created(Item __instance) { if (Tracking) { Local.Add(__instance); } }
    private static void ShallowRemoved(Item __instance) => Local.Remove(__instance);

    private static void Track(Item item)
    {
        if (item != null && !Local.Contains(item) && !Before.ContainsKey(item) && InventoryOps.Capture(item, out InventoryPlacement placement)) { Before.Add(item, placement); }
    }

    private static void Removing(Item item) { if (Tracking) { Track(item); } }
    private static void Dropping(Item __instance) { if (Tracking) { Track(__instance); } }

    private static void Dropped(Item __instance, Character dropper, bool setTransform)
    {
        if (!Tracking || !setTransform || __instance.ParentInventory != null || !Before.ContainsKey(__instance)) { return; }
        Vector2 world = Screen.Selected.Cam.ScreenToWorld(PlayerInput.MousePosition);
        InventoryPlacement placement = InventoryOps.CaptureDrop(__instance, world);
        WireTracker.Quietly(() =>
        {
            if (!InventoryOps.Apply(new[] { __instance.ID }, new[] { placement })) { ModLog.Error("subeditor: drop placement failed"); }
        });
    }

    private static bool Putting(Inventory __instance, Item item, int i, ref bool allowCombine, ref bool triggerOnInsertedEffects, ref bool __result)
    {
        if (!Tracking) { return true; }
        Track(item);
        if ((uint)i < (uint)__instance.Capacity)
        {
            foreach (Item existing in __instance.GetItemsAt(i)) { Track(existing); }
        }
        if (item?.ParentInventory is Inventory source && source.FindIndex(item) is int sourceSlot && sourceSlot >= 0)
        {
            foreach (Item stacked in source.GetItemsAt(sourceSlot)) { Track(stacked); }
        }
        bool insertNested = allowCombine;
        allowCombine = false;
        triggerOnInsertedEffects = false;
        if (insertNested && (uint)i < (uint)__instance.Capacity && __instance.GetItemAt(i) is Item container && container != item)
        {
            foreach (ItemInventory inventory in container.OwnInventories)
            {
                int slot = inventory.FindAllowedSlot(item);
                if (slot >= 0 && inventory.TryPutItem(item, slot, false, false, null, createNetworkEvent: false, triggerOnInsertedEffects: false))
                { __result = true; return false; }
            }
        }
        return true;
    }

    private static bool Open(SubEditorScreen __instance, Item item)
    {
        if (__instance is not LiveEditorScreen) { return true; }
        if (__instance.dummyCharacter == null || item == null || item.Removed) { return false; }
        Inventory.DraggingItems.Clear();
        __instance.OpenedItem = null;

        Vector2 itemSimPos = item.SimPosition;
        if (__instance.dummyCharacter.AnimController != null)
        {
            foreach (Limb limb in __instance.dummyCharacter.AnimController.Limbs)
            {
                limb.body.SetTransform(itemSimPos, 0.0f);
            }
            __instance.dummyCharacter.AnimController.Collider.SetTransform(itemSimPos, 0.0f);
        }

        if ((item.GetComponent<Holdable>() is { Attached: false } || item.GetComponent<Wearable>() != null) && item.GetComponent<ItemContainer>() != null)
        {
            oldHoldablePosition = item.SimPosition;
            var container = item.GetComponent<ItemContainer>();
            if (container != null) { container.KeepOpenWhenEquipped = true; }
            var allowedSlots = new List<InvSlotType>();
            foreach (InvSlotType type in item.AllowedSlots) { if (type != InvSlotType.Any) { allowedSlots.Add(type); } }
            if (__instance.dummyCharacter.Inventory.TryPutItem(item, __instance.dummyCharacter, allowedSlots))
            {
                __instance.OpenedItem = item;
            }
        }
        MapEntity.DeselectAll();
        MapEntity.SelectEntity(item);
        __instance.dummyCharacter.SelectedItem = item;
        if (__instance.entityFilterBox != null) { __instance.FilterEntities(__instance.entityFilterBox.Text); }
        MapEntity.StopSelection();
        lastOpenFrame = LiveEditorScreen.CurrentFrame;
        return false;
    }

    private static bool Close(SubEditorScreen __instance)
    {
        if (__instance is not LiveEditorScreen) { return true; }
        if (lastOpenFrame == LiveEditorScreen.CurrentFrame) { return false; }
        SubEditorScreen.DraggedItemPrefab = null;
        Inventory.DraggingItems.Clear();
        Inventory.selectedSlot = null;
        if (__instance.OpenedItem != null && __instance.dummyCharacter != null)
        {
            __instance.OpenedItem.Drop(__instance.dummyCharacter);
            __instance.OpenedItem.SetTransform(oldHoldablePosition, 0f);
        }
        if (__instance.dummyCharacter != null) { __instance.dummyCharacter.SelectedItem = null; }
        __instance.OpenedItem = null;
        if (__instance.entityFilterBox != null) { __instance.FilterEntities(__instance.entityFilterBox.Text); }
        return false;
    }

    public static void Forget(IEnumerable<MapEntity> entities)
    {
        foreach (MapEntity entity in entities) { if (entity is Item item) { Before.Remove(item); Local.Remove(item); } }
    }

    public static void Delete(IReadOnlyList<InventorySlotItem> items)
    {
        ushort[] ids = items.Where(row => row.Item != null && !row.Item.Removed && !Local.Contains(row.Item)).Select(row => row.Item.ID).Distinct().OrderBy(id => id).ToArray();
        if (ids.Length == 0) { return; }
        List<MapEntity> all = EntityOps.WithContents(ids);
        Deleted.Add(new Deletion(all.Select(entity => entity.ID).ToArray(), SubRegistry.WireId(all[0].Submarine), EntityOps.SaveXml(all)));
        Forget(all);
    }

    public static void QueueDelete(ushort[] ids, ushort submarine, byte[] xml) => Deleted.Add(new Deletion(ids, submarine, xml));

    public static void Flush()
    {
        if (WireTracker.Suppressed || SubEditorClient.State != LinkState.Live || Before.Count == 0 && Deleted.Count == 0) { return; }
        Changed.Clear();
        foreach (var pair in Before)
        {
            if (!pair.Key.Removed && InventoryOps.Capture(pair.Key, out InventoryPlacement current) && current != pair.Value) { Changed.Add(pair.Key); }
        }
        Changed.Sort(static (a, b) => a.ID.CompareTo(b.ID));
        ushort[] ids = Changed.Select(item => item.ID).ToArray();
        var old = new InventoryPlacement[ids.Length];
        var next = new InventoryPlacement[ids.Length];
        for (int i = 0; i < ids.Length; i++) { old[i] = Before[Changed[i]]; InventoryOps.Capture(Changed[i], out next[i]); }
        Before.Clear(); Changed.Clear();
        Deletion[] deleted = Deleted.ToArray(); Deleted.Clear();
        if (ids.Length == 0 && deleted.Length == 0) { return; }
        Action rollback = () => WireTracker.Quietly(() =>
        {
            foreach (Deletion deletion in deleted)
            {
                if (EntityOps.LoadExact(deletion.Xml, SubRegistry.Find(deletion.Submarine)) == null) { ModLog.Error("subeditor: deleted inventory could not be restored"); }
            }
            if (ids.Length != 0 && !InventoryOps.Apply(ids, old)) { ModLog.Error("subeditor: local inventory rollback failed"); }
        });
        var writer = new ByteWriter(128);
        try
        {
            OpKind kind = OpKind.Inventory;
            if (deleted.Length == 0) { InventoryPayload.Write(ref writer, next); }
            else
            {
                kind = OpKind.Batch;
                var operations = new List<BatchPayload.Sub>(deleted.Length + 1);
                foreach (Deletion deletion in deleted) { operations.Add(new BatchPayload.Sub(OpKind.Delete, deletion.Ids, Array.Empty<byte>())); }
                if (ids.Length != 0)
                {
                    var inventory = new ByteWriter(64);
                    try { InventoryPayload.Write(ref inventory, next); operations.Add(new BatchPayload.Sub(OpKind.Inventory, ids, inventory.Written.ToArray())); }
                    finally { inventory.Dispose(); }
                }
                BatchPayload.Write(ref writer, operations.ToArray());
            }
            if (!SubEditorClient.SendRequest(kind, kind == OpKind.Inventory ? ids : ReadOnlySpan<ushort>.Empty, writer.Written, rollback)) { rollback(); }
        }
        finally { writer.Dispose(); }
    }

    public static void Reset(bool restore = false)
    {
        WireTracker.Quietly(() =>
        {
            if (restore)
            {
                foreach (Deletion deletion in Deleted)
                {
                    if (EntityOps.LoadExact(deletion.Xml, SubRegistry.Find(deletion.Submarine)) == null) { ModLog.Error("subeditor: unsent deletion could not be restored"); }
                }
                var pending = Before.Where(pair => !pair.Key.Removed).OrderBy(pair => pair.Key.ID).ToArray();
                if (pending.Length != 0 && !InventoryOps.Apply(pending.Select(pair => pair.Key.ID).ToArray(), pending.Select(pair => pair.Value).ToArray()))
                { ModLog.Error("subeditor: unsent inventory changes could not be restored"); }
            }
            Item[] local = Local.ToArray(); Local.Clear();
            foreach (Item item in local) { if (!item.Removed) { item.Remove(); } }
        });
        Before.Clear(); Deleted.Clear(); Changed.Clear();
    }
}
