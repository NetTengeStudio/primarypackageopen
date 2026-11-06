using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionServer {
    private static void Flicker(IReadMessage message) {
        ushort itemId = message.ReadUInt16();
        byte component = message.ReadByte();
        if (Entity.FindEntityByID(itemId) is not Item { Removed: false } item || component >= item.Components.Count || item.Components[component] is not LightComponent light) {
            throw new ArgumentException("Источник света не найден");
        }
        Guid lease = Guid.NewGuid();
        LightEffectService.Instance.Start(light, LightEffectService.FlickerDuration, lease);
        INet.Net.Broadcast(InteractionProtocol.Channel, output => WriteFlicker(output, item, component, LightEffectService.FlickerDuration, lease));
    }
    private static void WriteFlicker(IWriteMessage output, Item item, byte component, float duration, Guid lease) {
        InteractionProtocol.Header(output, InteractionPacket.Flicker);
        output.WriteUInt16(item.ID); output.WriteByte(component); output.WriteSingle(duration); output.WriteString(lease.ToString("N"));
    }
    private static void SendActiveLights(Client recipient) {
        foreach (var effect in LightEffectService.Instance.Active) {
            Item item = effect.Component.Item;
            int index = item.Components.IndexOf(effect.Component);
            if ((uint)index > byte.MaxValue) { continue; }
            byte component = (byte)index;
            INet.Net.Send(InteractionProtocol.Channel, recipient.Connection, output => WriteFlicker(output, item, component, effect.Remaining, effect.Id));
        }
    }
}
