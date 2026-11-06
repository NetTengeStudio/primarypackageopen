using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Barotrauma;

namespace IEngine.StatusEffects;

static class ContainedTargets
{
    static readonly ConditionalWeakTable<ImmutableHashSet<Identifier>, Identifier[]> identifiers = new();
    static readonly ConditionalWeakTable<ImmutableHashSet<Identifier>, Identifier[]>.CreateValueCallback copyIdentifiers = source =>
    {
        var copy = new Identifier[source.Count];
        int index = 0;
        foreach (Identifier identifier in source) { copy[index++] = identifier; }
        return copy;
    };

    struct TagFilter
    {
        ImmutableHashSet<Identifier> source;
        Identifier[] values;

        public bool Any(Item item, ImmutableHashSet<Identifier> current)
        {
            ArgumentNullException.ThrowIfNull(current, "source");
            if (!ReferenceEquals(source, current))
            {
                source = current;
                values = identifiers.GetValue(current, copyIdentifiers);
            }
            Identifier[] tags = values;
            for (int i = 0; i < tags.Length; i++) { if (item.HasTag(tags[i])) { return true; } }
            return false;
        }
    }

    public static bool Append(Item owner, StatusEffect effect, List<ISerializableEntity> targets, bool specialize)
    {
        if (!specialize) { return AppendOriginal(owner, effect, targets); }
        bool matched = false;
        TagFilter filter = default;
        if (owner.OwnInventories.Length < 2)
        {
            if (owner.OwnInventory != null) { AppendInventory(owner, owner.OwnInventory, effect, ref filter, targets, ref matched); }
        }
        else
        {
            foreach (ItemInventory inventory in owner.OwnInventories) { AppendInventory(owner, inventory, effect, ref filter, targets, ref matched); }
        }
        return matched;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining | System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
    public static IEnumerable<Item> ReadContents(Item owner) => owner.ContainedItems;

    static void AppendInventory(Item owner, ItemInventory inventory, StatusEffect effect, ref TagFilter filter, List<ISerializableEntity> targets, ref bool matched)
    {
        if (inventory.GetType() != typeof(ItemInventory))
        {
            foreach (Item child in inventory.AllItems) { AppendChild(owner, child, effect, ref filter, targets, ref matched); }
            return;
        }
        for (int slot = 0; slot < inventory.capacity; slot++)
        {
            IReadOnlyList<Item> items = inventory.slots[slot].Items;
            for (int index = 0; index < items.Count; index++)
            {
                Item child = items[index];
                if (child != null) { AppendChild(owner, child, effect, ref filter, targets, ref matched); }
            }
        }
    }

    static void AppendChild(Item owner, Item child, StatusEffect effect, ref TagFilter filter, List<ISerializableEntity> targets, ref bool matched)
    {
        if (effect.TargetIdentifiers != null && !effect.TargetIdentifiers.Contains(((MapEntity)child).Prefab.Identifier) &&
            !filter.Any(child, effect.TargetIdentifiers)) { return; }
        if (effect.TargetSlot > -1 && !owner.OwnInventory.GetItemsAt(effect.TargetSlot).Contains(child)) { return; }
        matched = true;
        targets.AddRange(child.AllPropertyObjects);
    }

    static bool AppendOriginal(Item owner, StatusEffect effect, List<ISerializableEntity> targets)
    {
        bool matched = false;
        foreach (Item child in ReadContents(owner))
        {
            if (effect.TargetIdentifiers != null && !effect.TargetIdentifiers.Contains(((MapEntity)child).Prefab.Identifier) &&
                !effect.TargetIdentifiers.Any(identifier => child.HasTag(identifier))) { continue; }
            if (effect.TargetSlot > -1 && !owner.OwnInventory.GetItemsAt(effect.TargetSlot).Contains(child)) { continue; }
            matched = true;
            targets.AddRange(child.AllPropertyObjects);
        }
        return matched;
    }
}
