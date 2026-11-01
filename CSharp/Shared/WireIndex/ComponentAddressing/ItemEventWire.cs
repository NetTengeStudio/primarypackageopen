using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;

namespace PrimaryPackage.Netcode;

public static class ItemEventWire {
    private const int TargetItem = 0;
    private const int TargetComponent = 1;
    private const int TargetOther = 2;

    public static readonly int ActionTypes = Enum.GetValues(typeof(ActionType)).Length;

    public static Item.EventType ReadType(IReadMessage msg) =>
        (Item.EventType)msg.ReadRangedInteger((int)Item.EventType.MinValue, (int)Item.EventType.MaxValue);

    public static void WriteType(IWriteMessage msg, Item.EventType type) =>
        msg.WriteRangedInteger((int)type, (int)Item.EventType.MinValue, (int)Item.EventType.MaxValue);

    public static void WriteUpgradeTarget(IWriteMessage msg, Item item, ISerializableEntity target) {
        if (ReferenceEquals(target, item)) {
            msg.WriteRangedInteger(TargetItem, TargetItem, TargetOther);
            return;
        }
        if (target is ItemComponent component && ComponentAddress.TryOf(item.Components, component, out ComponentAddress address)) {
            msg.WriteRangedInteger(TargetComponent, TargetItem, TargetOther);
            address.Write(msg);
            return;
        }
        msg.WriteRangedInteger(TargetOther, TargetItem, TargetOther);
    }

    public static ISerializableEntity ReadUpgradeTarget(IReadMessage msg, Item item) {
        int kind = msg.ReadRangedInteger(TargetItem, TargetOther);
        if (kind == TargetItem) { return item; }
        if (kind == TargetComponent) { return ComponentAddress.Read(msg).Resolve(item.Components); }
        return null;
    }
}
