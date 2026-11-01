using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Netcode;

public static class WireSites {
    private static FieldInfo powerGroupsField;

    public static void WriteComponent(IWriteMessage msg, int index, int min, int max, Item item) =>
        WireRefCodec.Write(msg, index, new ComponentKeys(item.Components));

    public static int ReadComponent(IReadMessage msg, int min, int max, Item item) {
        int index = WireRefCodec.Read(msg, new ComponentKeys(item.Components));
        if (index >= 0) { return index; }
        ModLog.Once("item component is not on this side");
        return WireBounds.Clamp(0, min, max);
    }

    public static int PeekComponent(IReadMessage msg, Item item) =>
        WireRefCodec.Peek(msg, new ComponentKeys(item.Components));

    public static void WriteSignal(IWriteMessage msg, int index, int min, int max, ButtonTerminal terminal) =>
        WireRefCodec.Write(msg, index, new StringKeys(terminal.Signals));

    public static int ReadSignal(IReadMessage msg, int min, int max, ButtonTerminal terminal) {
        int index = WireRefCodec.Read(msg, new StringKeys(terminal.Signals));
        if (index >= 0) { return index; }
        ModLog.Once("button signal is not on this side");
        return WireBounds.Clamp(0, min, max);
    }

    public static int PeekSignal(IReadMessage msg, ButtonTerminal terminal) =>
        WireRefCodec.Peek(msg, new StringKeys(terminal.Signals));

    public static void WritePowerGroup(IWriteMessage msg, int index, int min, int max, PowerDistributor distributor) =>
        WireRefCodec.Write(msg, index, Groups(distributor));

    public static int ReadPowerGroup(IReadMessage msg, int min, int max, PowerDistributor distributor) {
        int index = WireRefCodec.Read(msg, Groups(distributor));
        if (index >= 0) { return index; }
        ModLog.Once("power group is not on this side");
        return WireBounds.Clamp(0, min, max);
    }

    public static KeyValuePair<ISerializableEntity, PropertyReference[]> UpgradeTargetAt(
        IEnumerable<KeyValuePair<ISerializableEntity, PropertyReference[]>> targets, int index) {
        if (targets is IList<KeyValuePair<ISerializableEntity, PropertyReference[]>> list) {
            return index >= 0 && index < list.Count ? list[index] : default;
        }
        int position = 0;
        foreach (var target in targets) {
            if (position++ == index) { return target; }
        }
        return default;
    }

    public static PropertyReference PropertyAt(PropertyReference[] properties, int index) =>
        properties != null && index >= 0 && index < properties.Length ? properties[index] : null;

    public static void SetIfPresent(PropertyReference property, object value) => property?.SetOriginalValue(value);

    public static LevelObject LevelObjectAt(List<LevelObject> objects, int index) =>
        objects != null && index >= 0 && index < objects.Count ? objects[index] : null;

    public static void Clear() {
        powerGroupsField = null;
        PowerGroupName.Clear();
    }

    private static NamedKeys Groups(PowerDistributor distributor) {
        powerGroupsField ??= AccessTools.Field(typeof(PowerDistributor), "powerGroups");
        return new NamedKeys(powerGroupsField?.GetValue(distributor) as IList, PowerGroupName.Of);
    }
}

public static class WireGuard {
    public static bool ItemEventPresent(Item item, IReadMessage msg) {
        int position = msg.BitPosition;
        Item.EventType type = ItemEventWire.ReadType(msg);
        bool present = true;
        if (type == Item.EventType.ComponentState || type == Item.EventType.InventoryState) {
            present = WireSites.PeekComponent(msg, item) >= 0;
        }
        msg.BitPosition = position;
        return present;
    }

    public static bool SignalPresent(ButtonTerminal terminal, IReadMessage msg) => WireSites.PeekSignal(msg, terminal) >= 0;
}
