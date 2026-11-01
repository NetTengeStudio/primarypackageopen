using System;
using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public readonly struct WorldQueryResult {
    public readonly Entity Entity;
    public readonly float DistanceSquared;
    public WorldQueryResult(Entity entity, float distanceSquared) { Entity = entity; DistanceSquared = distanceSquared; }
}

public sealed class WorldQueryService {
    private sealed class Frame {
        public readonly Submarine Submarine;
        public readonly SpatialIndex<Entity> Index = new();
        public Frame(Submarine submarine) { Submarine = submarine; }
    }
    private sealed class DistanceOrder : IComparer<WorldQueryResult> {
        public int Compare(WorldQueryResult x, WorldQueryResult y) {
            int order = x.DistanceSquared.CompareTo(y.DistanceSquared);
            return order == 0 ? x.Entity.ID.CompareTo(y.Entity.ID) : order;
        }
    }
    private readonly Dictionary<ushort, Frame> frames = new();
    private readonly Dictionary<Entity, Frame> membership = new();
    private readonly List<Entity> candidates = new();
    private readonly List<Entity> pending = new();
    private readonly Dictionary<Entity, int> pendingSlots = new();
    private readonly List<WorldQueryResult> inventoryWork = new();
    private readonly HashSet<Entity> visited = new();
    private static readonly DistanceOrder order = new();
    private bool seeded;
    public static WorldQueryService Instance { get; } = new();
    public long Updates { get; private set; }
    public int Count => membership.Count;

    public void Attach(Harmony harmony) {
        foreach (var constructor in AccessTools.GetDeclaredConstructors(typeof(Item))) { if (!constructor.IsStatic) { harmony.Patch(constructor, postfix: ModuleHost.Bind(typeof(WorldQueryService), nameof(ItemCreated))); } }
        foreach (var constructor in AccessTools.GetDeclaredConstructors(typeof(Character))) { if (!constructor.IsStatic) { harmony.Patch(constructor, postfix: ModuleHost.Bind(typeof(WorldQueryService), nameof(CharacterCreated))); } }
        harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.UpdateTransform)), postfix: ModuleHost.Bind(typeof(WorldQueryService), nameof(ItemMoved)));
        harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.SetTransform)), postfix: ModuleHost.Bind(typeof(WorldQueryService), nameof(ItemMoved)));
        harmony.Patch(AccessTools.PropertySetter(typeof(Item), nameof(Item.ParentInventory)), postfix: ModuleHost.Bind(typeof(WorldQueryService), nameof(ItemMoved)));
        harmony.Patch(AccessTools.Method(typeof(PhysicsBody), nameof(PhysicsBody.SetPrevTransform)), postfix: ModuleHost.Bind(typeof(WorldQueryService), nameof(BodyMoving)));
        harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Update)), postfix: ModuleHost.Bind(typeof(WorldQueryService), nameof(CharacterMoved)));
        harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.TeleportTo)), postfix: ModuleHost.Bind(typeof(WorldQueryService), nameof(CharacterMoved)));
        harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Remove)), prefix: ModuleHost.Bind(typeof(WorldQueryService), nameof(Removing)));
        harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Remove)), prefix: ModuleHost.Bind(typeof(WorldQueryService), nameof(Removing)));
    }
    public static void ItemCreated(Item __instance) { if (Instance.seeded) { Instance.MarkDirty(__instance); } }
    public static void CharacterCreated(Character __instance) { if (Instance.seeded) { Instance.MarkDirty(__instance); } }
    public static void ItemMoved(Item __instance) { if (Instance.seeded) { Instance.MarkDirty(__instance); } }
    public static void CharacterMoved(Character __instance) { if (Instance.seeded) { Instance.MarkDirty(__instance); } }
    public static void BodyMoving(PhysicsBody __instance) { if (Instance.seeded && !__instance.Removed && __instance.UserData is Entity entity && (entity is Item || entity is Character)) { Instance.MarkDirty(entity); } }
    public static void Removing(Entity __instance) => Instance.Remove(__instance);

    private void Upsert(Entity entity) {
        if (entity.Removed || entity is Item { ParentInventory: not null } || entity is not Item && entity is not Character) { Remove(entity); return; }
        Submarine submarine = entity.Submarine;
        ushort id = submarine?.ID ?? 0;
        if (!frames.TryGetValue(id, out var frame)) { frame = new Frame(submarine); frames.Add(id, frame); }
        if (membership.TryGetValue(entity, out var previous) && previous != frame) { previous.Index.Remove(entity); }
        Vector2 position = entity.WorldPosition - (submarine?.WorldPosition ?? Vector2.Zero);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) { Remove(entity); return; }
        frame.Index.Upsert(entity, position.X, position.Y);
        membership[entity] = frame;
        Updates++;
    }
    private void Remove(Entity entity) {
        if (membership.Remove(entity, out var frame)) { frame.Index.Remove(entity); }
        if (pendingSlots.Remove(entity, out int index)) {
            Entity last = pending[pending.Count - 1];
            if (index != pending.Count - 1) { pending[index] = last; pendingSlots[last] = index; }
            pending.RemoveAt(pending.Count - 1);
        }
    }
    private void MarkDirty(Entity entity) {
        if (entity.Removed) { Remove(entity); return; }
        if (entity is Item { ParentInventory: not null }) { Remove(entity); return; }
        if (pendingSlots.TryAdd(entity, pending.Count)) { pending.Add(entity); }
    }
    private void Flush() {
        while (pending.Count > 0) {
            Entity entity = pending[pending.Count - 1];
            pendingSlots.Remove(entity);
            pending.RemoveAt(pending.Count - 1);
            Upsert(entity);
        }
    }
    public void Query(Vector2 world, float radius, bool characters, string filter, List<WorldQueryResult> results, int limit = InteractionProtocol.MaxQueryResults) {
        if (!float.IsFinite(world.X) || !float.IsFinite(world.Y) || !float.IsFinite(radius) || radius <= 0 || radius > 5000 || limit < 1 || limit > 10000) { throw new ArgumentOutOfRangeException(nameof(radius)); }
        if (!seeded) {
            seeded = true;
            foreach (Item item in Item.ItemList) { Upsert(item); }
            foreach (Character character in Character.CharacterList) { Upsert(character); }
        }
        Flush();
        candidates.Clear();
        inventoryWork.Clear();
        visited.Clear();
        results.Clear();
        foreach (var frame in frames.Values) {
            if (frame.Submarine is { Removed: true }) { continue; }
            Vector2 local = world - (frame.Submarine?.WorldPosition ?? Vector2.Zero);
            frame.Index.Query(local.X, local.Y, radius, candidates);
        }
        float squared = radius * radius;
        for (int i = 0; i < candidates.Count; i++) {
            Entity entity = candidates[i];
            if (entity.Removed) { continue; }
            float distance = Vector2.DistanceSquared(world, entity.WorldPosition);
            if (distance > squared) { continue; }
            if (characters) {
                if (entity is Character character && Matches(character.Name, filter)) { results.Add(new WorldQueryResult(entity, distance)); }
            } else { inventoryWork.Add(new WorldQueryResult(entity, distance)); }
        }
        for (int i = 0; i < inventoryWork.Count; i++) {
            var entry = inventoryWork[i];
            if (entry.Entity.Removed || !visited.Add(entry.Entity)) { continue; }
            if (entry.Entity is Item item) {
                if (Matches(item.Name, filter)) { results.Add(entry); }
                if (item.OwnInventories.Length < 2) { AppendInventory(item.OwnInventory, entry.DistanceSquared); }
                else { for (int inventory = 0; inventory < item.OwnInventories.Length; inventory++) { AppendInventory(item.OwnInventories[inventory], entry.DistanceSquared); } }
            } else if (entry.Entity is Character owner) { AppendInventory(owner.Inventory, entry.DistanceSquared); }
        }
        results.Sort(order);
        if (results.Count > limit) { results.RemoveRange(limit, results.Count - limit); }
    }
    private static bool Matches(string name, string filter) => string.IsNullOrEmpty(filter) || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    private void AppendInventory(Inventory inventory, float distance) {
        if (inventory == null) { return; }
        for (int slot = 0; slot < inventory.Capacity; slot++) {
            var contents = inventory.GetItemsAt(slot);
            if (contents is IReadOnlyList<Item> items) { for (int i = 0; i < items.Count; i++) { inventoryWork.Add(new WorldQueryResult(items[i], distance)); } }
            else { foreach (Item item in contents) { inventoryWork.Add(new WorldQueryResult(item, distance)); } }
        }
    }
    public void Clear() { seeded = false; candidates.Clear(); membership.Clear(); frames.Clear(); pending.Clear(); pendingSlots.Clear(); inventoryWork.Clear(); visited.Clear(); }
}
