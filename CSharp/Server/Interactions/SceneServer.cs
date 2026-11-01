using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionServer {
    private readonly SceneRuntime scenes = new();
    private SceneWorld sceneWorld;
    private double nextSceneTick;
    private ulong publishedSceneVersion;
    private void PublishSceneChanges() {
        if (publishedSceneVersion == scenes.Version) { return; }
        publishedSceneVersion = scenes.Version;
        foreach (Session session in sessions.Values) { if (permissions.Allows(session.Client, InteractionAction.DirectEvents)) { SendScenes(session.Client); } }
    }
    private void InitializeScenes() => sceneWorld = new SceneWorld(
        target => FindAccount(target)?.Character,
        (owner, action) => permissions.Allows(FindAccount(owner), action), PublishDirector, PublishLight);

    private void PublishLight(Guid id, Item item, byte component, float duration) {
        if (duration == 0) { LightEffectService.Instance.Cancel(id); }
        else {
            if (item is not { Removed: false } || component >= item.Components.Count || item.Components[component] is not LightComponent source) { throw new ArgumentException("Лампа больше недоступна"); }
            LightEffectService.Instance.Start(source, duration, id);
        }
        ModNet.Broadcast(InteractionProtocol.Channel, output => {
            InteractionProtocol.Header(output, InteractionPacket.Flicker); output.WriteUInt16(item?.ID ?? 0); output.WriteByte(component); output.WriteSingle(duration); output.WriteString(id.ToString("N"));
        });
    }
    private void PublishDirector(DirectorEvent value, string actor) {
        DirectorEvent.Validate(value);
        if (value.Kind == DirectorKind.SignalCollapse && value.Target.Length == 0) { throw new ArgumentException("Signal Collapse требует выбранного игрока"); }
        double now = Now;
        if (value.Kind == DirectorKind.Stop) {
            if (value.Target.Length == 0) { scenes.Clear(); }
            expiredIds.Clear();
            foreach (var entry in activeDirected) { if (value.Target.Length == 0 || entry.Value.Event.Target == value.Target) { expiredIds.Add(entry.Key); } }
            foreach (string id in expiredIds) { activeDirected.Remove(id); }
        } else if (value.Kind == DirectorKind.StopOne) { activeDirected.Remove(value.Resource); }
        else {
            if (value.Target.Length > 0 && FindAccount(value.Target)?.Character is not { Removed: false }) { throw new ArgumentException("Игрок отключился или не находится в мире"); }
            if (value.Submarine != 0 && Entity.FindEntityByID(value.Submarine) is not Submarine { Removed: false }) { throw new ArgumentException("Подлодка сцены больше недоступна"); }
            PruneDirected(now);
            if (activeDirected.Count >= 16 || activeDirected.ContainsKey(value.Id)) { throw new InvalidOperationException("Достигнут предел 16 эффектов или повторён идентификатор"); }
            activeDirected.Add(value.Id, new DirectedState { Actor = actor, Event = value, End = now + value.Delay + value.Duration });
        }
        long sequence = ++directorSequence;
        var server = GameMain.Server;
        if (server != null) {
            foreach (Client connected in server.ConnectedClients) {
                if (value.Target.Length > 0 && PermissionService.Account(connected) != value.Target) { continue; }
                ModNet.Send(InteractionProtocol.Channel, connected.Connection, output => { InteractionProtocol.Header(output, InteractionPacket.DirectorEvent); output.WriteInt64(sequence); DirectorEvent.Write(output, value); });
            }
        }
        if (value.Kind == DirectorKind.Stop && value.Target.Length == 0) {
            LightEffectService.Instance.Clear();
            ModNet.Broadcast(InteractionProtocol.Channel, output => { InteractionProtocol.Header(output, InteractionPacket.Flicker); output.WriteUInt16(0); output.WriteByte(0); output.WriteSingle(0); output.WriteString(Guid.Empty.ToString("N")); });
        }
    }
    private void SendScenes(Client client) => ModNet.Send(InteractionProtocol.Channel, client.Connection, output => {
        InteractionProtocol.Header(output, InteractionPacket.SceneStatus); output.WriteByte((byte)scenes.Entries.Count);
        foreach (SceneRuntime.Entry entry in scenes.Entries) {
            output.WriteString(entry.Id.ToString("N")); output.WriteUInt32(entry.Revision); output.WriteString(entry.Name); output.WriteString(entry.Owner);
            output.WriteByte((byte)entry.Trigger); output.WriteByte((byte)entry.State); output.WriteString(entry.Error);
        }
    });
}
