using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

public static class InventoryOps
{
    private readonly record struct BodyState(bool Present, bool Enabled, bool Physical, FarseerPhysics.BodyType Type, Vector2 Velocity, float AngularVelocity);
    private static bool EditorAccess => WorldContext.Active
#if CLIENT
        || SubEditorClient.Editing && LiveEditorScreen.EditorUpdating
#endif
        ;

    public static void Attach(Harmony harmony)
    {
        harmony.Patch(AccessTools.PropertyGetter(typeof(ItemContainer), nameof(ItemContainer.AllowAccessWhenDropped)),
            postfix: new HarmonyMethod(typeof(InventoryOps), nameof(Access)));
        harmony.Patch(AccessTools.Method(typeof(ItemContainer), "SpawnAlwaysContainedItems"), prefix: new HarmonyMethod(typeof(InventoryOps), nameof(SpawnContents)));
    }

    private static void Access(ref bool __result) { if (EditorAccess) { __result = true; } }
    private static bool SpawnContents(ItemContainer __instance)
    {
        if (!EditorAccess) { return true; }
        __instance.alwaysContainedItemsSpawned = true;
        return false;
    }

    public static bool Capture(Item item, out InventoryPlacement placement)
    {
        placement = default;
        if (item == null || item.Removed) { return false; }
        Inventory inventory = item.ParentInventory;
        if (inventory == null)
        {
            Rectangle rect = EntityOps.LocalRect(item.Rect, item.Submarine);
            placement = new InventoryPlacement(0, -1, -1, SubRegistry.WireId(item.Submarine), rect.X, rect.Y, item.RotationRad);
            return true;
        }
        if (inventory is not ItemInventory owned || inventory.Owner is not Item container) { return false; }
        int component = container.Components.IndexOf(owned.Container), slot = inventory.FindIndex(item);
        if (component < 0 || component > short.MaxValue || slot < 0) { return false; }
        placement = new InventoryPlacement(container.ID, (short)component, slot);
        return true;
    }

    /// <summary>Places a dropped item's center at the cursor in the containing hull's frame, or the world outside all hulls.</summary>
    public static InventoryPlacement CaptureDrop(Item item, Vector2 world)
    {
        Submarine owner = Hull.FindHull(world)?.Submarine;
        Vector2 local = world - (owner?.WorldPosition ?? Vector2.Zero);
        return new InventoryPlacement(0, -1, -1, SubRegistry.WireId(owner),
            (int)(local.X - item.Rect.Width / 2f), (int)(local.Y + item.Rect.Height / 2f), item.RotationRad);
    }

    public static ItemInventory Destination(in InventoryPlacement placement)
    {
        if (placement.ContainerId == 0 || EntityOps.Find(placement.ContainerId) is not Item { Removed: false } container ||
            (uint)placement.ComponentIndex >= (uint)container.Components.Count) { return null; }
        return container.Components[placement.ComponentIndex] is ItemContainer { Inventory: { } inventory } &&
            (uint)placement.Slot < (uint)inventory.Capacity ? inventory : null;
    }

    public static bool Apply(ReadOnlySpan<ushort> ids, ReadOnlySpan<InventoryPlacement> placements)
    {
        using var world = WorldContext.Enter();
        if (ids.Length != placements.Length || ids.Length > OpFrame.MaxTouched || !OpFrame.AreStrictlyAscending(ids)) { return false; }
        var items = new Item[ids.Length];
        var old = new InventoryPlacement[ids.Length];
        var destinations = new ItemInventory[ids.Length];
        bool changed = false;
        for (int i = 0; i < ids.Length; i++)
        {
            if (EntityOps.Find(ids[i]) is not Item item || !Capture(item, out old[i])) { return false; }
            items[i] = item;
            changed |= old[i] != placements[i];
            ref readonly InventoryPlacement p = ref placements[i];
            if (p.ContainerId == 0)
            {
                if (p.ComponentIndex != -1 || p.Slot != -1 || !float.IsFinite(p.Rotation) ||
                    p.SubmarineId != SubRegistry.WorldId && SubRegistry.Find(p.SubmarineId) == null) { return false; }
            }
            else if ((destinations[i] = Destination(p)) == null) { return false; }
        }
        if (!changed) { Refresh(items); return true; }
        var path = new HashSet<ushort>();
        for (int i = 0; i < ids.Length; i++)
        {
            path.Clear();
            Item at = items[i];
            while (at != null)
            {
                if (!path.Add(at.ID)) { return false; }
                int moving = MemoryExtensions.BinarySearch(ids, at.ID);
                at = moving >= 0 ? destinations[moving]?.Owner as Item : at.ParentInventory?.Owner as Item;
            }
        }
        var bodies = new BodyState[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].body is PhysicsBody body) { bodies[i] = new BodyState(true, body.Enabled, body.PhysEnabled, body.FarseerBody.BodyType, body.LinearVelocity, body.AngularVelocity); }
        }
        bool committed = false;
        try
        {
            Detach(items);
            for (int i = 0; i < items.Length; i++) { if (!Place(items[i], placements[i], destinations[i])) { return false; } }
            Refresh(items);
            committed = true;
            return true;
        }
        finally
        {
            if (!committed)
            {
                Detach(items);
                for (int i = 0; i < items.Length; i++)
                {
                    if (!Place(items[i], old[i], Destination(old[i]))) { ModLog.Error("subeditor: inventory rollback could not place item"); }
                }
                Refresh(items);
                for (int i = 0; i < items.Length; i++)
                {
                    if (!bodies[i].Present) { continue; }
                    PhysicsBody body = items[i].body;
                    BodyState source = bodies[i];
                    body.Enabled = source.Enabled; body.PhysEnabled = source.Physical;
                    body.FarseerBody.BodyType = source.Type;
                    body.LinearVelocity = source.Velocity; body.AngularVelocity = source.AngularVelocity;
                }
            }
        }
    }

    private static void Detach(Item[] items)
    {
        foreach (Item item in items) { if (item.ParentInventory != null) { item.Drop(null, createNetworkEvent: false, setTransform: false); } }
    }

    private static bool Place(Item item, in InventoryPlacement placement, ItemInventory destination)
    {
        if (placement.ContainerId != 0)
        {
            return destination != null && destination.TryPutItem(item, placement.Slot, allowSwapping: false, allowCombine: false,
                user: null, createNetworkEvent: false, triggerOnInsertedEffects: false) && item.ParentInventory == destination && destination.FindIndex(item) == placement.Slot;
        }
        if (!EntityOps.SetFrame(item, placement.SubmarineId)) { return false; }
        Rectangle rect = EntityOps.LocalRect(item.Rect, item.Submarine);
        rect.X = placement.X; rect.Y = placement.Y;
        item.Rotation = MathHelper.ToDegrees(placement.Rotation);
        EntityOps.SetLocalRect(item, rect);
        return true;
    }

    private static void Refresh(Item[] items)
    {
        foreach (Item item in items)
        {
            if (item.ParentInventory?.Owner is Item container) { container.SetContainedItemPositions(); }
            else { item.SetContainedItemPositions(); }
        }
    }

    public static ushort[] Touched(ReadOnlySpan<ushort> ids, ReadOnlySpan<InventoryPlacement> before, ReadOnlySpan<InventoryPlacement> after)
    {
        var touched = new SortedSet<ushort>();
        for (int i = 0; i < ids.Length; i++)
        {
            AddFamily(EntityOps.Find(ids[i]) as Item, touched);
            if (before.Length > i) { AddAncestors(EntityOps.Find(before[i].ContainerId) as Item, touched); }
            if (after.Length > i) { AddAncestors(EntityOps.Find(after[i].ContainerId) as Item, touched); }
        }
        var result = new ushort[touched.Count];
        touched.CopyTo(result);
        return result;
    }

    private static void AddFamily(Item item, SortedSet<ushort> ids)
    {
        if (item == null || !ids.Add(item.ID)) { return; }
        foreach (Item contained in item.ContainedItems) { AddFamily(contained, ids); }
    }

    private static void AddAncestors(Item item, SortedSet<ushort> ids)
    {
        while (item != null)
        {
            ids.Add(item.ID);
            item = item.ParentInventory?.Owner as Item;
        }
    }
}
