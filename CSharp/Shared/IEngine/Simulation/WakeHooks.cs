using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using MoonSharp.Interpreter.Interop;

namespace IEngine.Simulation;

static class WakeHooks
{
    public static bool Armed;
    public static readonly List<string> Problems = new();

    static bool installed;

    public static void Install(Harmony h)
    {
        if (installed) { return; }
        installed = true;
        Patch(h, AccessTools.PropertySetter(typeof(Item), nameof(Item.ParentInventory)), nameof(ParentPre), nameof(ParentPost));
        Patch(h, AccessTools.Method(typeof(Item), "SetCondition"), nameof(ConditionPre), nameof(ConditionPost));
        foreach (MethodInfo m in typeof(SerializableProperty).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (m.Name is not ("TrySetValue" or "SetValue")) { continue; }
            var p = m.GetParameters();
            if (p.Length != 2 || p[0].ParameterType != typeof(object)) { continue; }
            Patch(h, m, nameof(PropertyPre), nameof(PropertyPost));
        }
        Patch(h, AccessTools.PropertySetter(typeof(Item), nameof(Item.Tags)), null, nameof(TagsPost));
        foreach (MethodInfo m in typeof(Item).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (m.Name is "AddTag" or "RemoveTag" or "ReplaceTag") { Patch(h, m, null, nameof(TagsPost)); }
        }
        Patch(h, AccessTools.PropertySetter(typeof(ItemComponent), nameof(ItemComponent.IsActive)), nameof(ComponentActivePre), nameof(ComponentActivePost));
        foreach (ConstructorInfo c in typeof(DurationListElement).GetConstructors()) { Patch(h, c, null, nameof(ListElementPost)); }
        foreach (ConstructorInfo c in typeof(DelayedListElement).GetConstructors()) { Patch(h, c, null, nameof(ListElementPost)); }
        Patch(h, AccessTools.Method(typeof(PropertyMemberDescriptor), "SetValue"), null, nameof(LuaSetPost));
        Patch(h, AccessTools.Method(typeof(FieldMemberDescriptor), "SetValue"), null, nameof(LuaSetPost));
        Patch(h, AccessTools.Method(typeof(Connection), nameof(Connection.ConnectWire)), null, nameof(WireConnected));
        Patch(h, AccessTools.Method(typeof(Connection), nameof(Connection.DisconnectWire)), null, nameof(WireDisconnected));
        Patch(h, AccessTools.Method(typeof(Wire), nameof(Wire.RemoveConnection), new[] { typeof(Connection) }),
            null, nameof(WireTopologyChanged));
        Patch(h, AccessTools.Method(typeof(Barotrauma.LuaCs.EventService), "Add", new[] { typeof(string), typeof(string), typeof(LuaCsFunc), typeof(object) }), null, nameof(LuaHookAdded));
    }

    static void Patch(Harmony h, MethodBase target, string prefix, string postfix)
    {
        if (target == null)
        {
            Problems.Add($"hook target missing for {postfix}");
            return;
        }
        try
        {
            h.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(WakeHooks), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(WakeHooks), postfix));
        }
        catch (Exception e) { Problems.Add($"{target.DeclaringType?.Name}.{target.Name}: {e.Message}"); }
    }

    static bool Watching => (Armed || SpatialCore.On) && (Dormancy.Count > 0 || Dormancy.Probing != null);

    static Item OwnerOf(object o) => o switch
    {
        Item i => i,
        ItemComponent c => c.Item,
        _ => null
    };

    public static void WakeNear(Item item)
    {
        if (item == null) { return; }
        Dormancy.Wake(item);
        for (Item c = item.Container; c != null; c = c.Container) { Dormancy.Wake(c); }
        WakeContents(item, false);
    }

    static void WakeContents(Item item, bool recursive)
    {
        if (item.OwnInventory == null) { return; }
        List<ItemComponent> components = item.components;
        for (int c = 0; c < components.Count; c++)
        {
            if (components[c] is not ItemContainer container) { continue; }
            Inventory inventory = container.Inventory;
            for (int s = 0; s < inventory.capacity; s++)
            {
                List<Item> items = inventory.slots[s].items;
                for (int i = 0; i < items.Count; i++)
                {
                    Item contained = items[i];
                    Dormancy.Wake(contained);
                    if (recursive) { WakeContents(contained, true); }
                }
            }
        }
    }

    static void WakeOwnerChain(Inventory inventory)
    {
        if (inventory?.Owner is not Item owner) { return; }
        for (Item c = owner; c != null; c = c.Container) { Dormancy.Wake(c); }
    }

    static void ParentPre(Item __instance, out Inventory __state) => __state = __instance.parentInventory;

    static void ParentPost(Item __instance, Inventory __state)
    {
        // Changing the containment edge may split an existing dependency island.
        if (SpatialCore.On && !ReferenceEquals(__state, __instance.parentInventory)) { SpatialCore.TopologyChanged(); }
        Dormancy.Source = 1;
        if (!(Armed || SpatialCore.On) || ReferenceEquals(__state, __instance.parentInventory)) { return; }
        Dormancy.MarkDirty("inventory");
        Dormancy.Wake(__instance);
        WakeContents(__instance, true);
        WakeOwnerChain(__state);
        WakeOwnerChain(__instance.parentInventory);
    }

    static void ConditionPre(Item __instance, out float __state) => __state = __instance.condition;

    static void ConditionPost(Item __instance, float __state)
    {
        Dormancy.Source = 2;
        if (!Watching || __state == __instance.condition) { return; }
        Dormancy.MarkDirty("condition");
        WakeNear(__instance);
    }

    static void PropertyPre(SerializableProperty __instance, object parentObject, out object __state)
    {
        __state = null;
        if (!Watching || OwnerOf(parentObject) is not { } owner) { return; }
        if (Dormancy.Probing != null || IsDormant(owner)) { __state = __instance.GetValue(parentObject) ?? Unset; }
    }

    static readonly object Unset = new();

    static void PropertyPost(SerializableProperty __instance, object parentObject, object __state)
    {
        Dormancy.Source = 3;
        if (__state == null) { return; }
        object now = __instance.GetValue(parentObject) ?? Unset;
        if (Equals(now, __state)) { return; }
        Dormancy.MarkDirty($"property {parentObject.GetType().Name}.{__instance.Name}");
        WakeNear(OwnerOf(parentObject));
    }

    static bool IsDormant(Item item) => Execution.ItemRegistry.TryGetSlot(item, out int slot) && Dormancy.Dormant.Get(slot);

    static void TagsPost(Item __instance)
    {
        Dormancy.Source = 4;
        if (!Watching) { return; }
        Dormancy.MarkDirty("tags");
        WakeNear(__instance);
    }

    static void ComponentActivePre(ItemComponent __instance, out bool __state) => __state = __instance.isActive;

    static void ComponentActivePost(ItemComponent __instance, bool __state)
    {
        Dormancy.Source = 5;
        if (!Watching || __state == __instance.isActive) { return; }
        Dormancy.MarkDirty($"{__instance.GetType().Name}.IsActive");
        Dormancy.Wake(__instance.Item);
    }

    static void ListElementPost()
    {
        if (Armed) { Dormancy.MarkDirty("status list element"); }
    }

    static void LuaSetPost(object obj)
    {
        Dormancy.Source = 6;
        if (Watching && OwnerOf(obj) is { } owner) { WakeNear(owner); }
    }

    static void WireConnected(Connection __instance, Wire wire)
    {
        if (SpatialCore.On && !__instance.IsPower)
        {
            SpatialCore.WiringChanged(__instance.Item, wire?.OtherConnection(__instance)?.Item);
        }
    }

    static void WireDisconnected() => SpatialCore.TopologyChanged();

    static void WireTopologyChanged() => SpatialCore.TopologyChanged();

    static void LuaHookAdded(string eventName)
    {
        if (Armed && eventName != null && eventName.StartsWith("statusEffect.apply.", StringComparison.OrdinalIgnoreCase)) { Dormancy.WakeAll(Dormancy.ByContainer); }
    }
}
