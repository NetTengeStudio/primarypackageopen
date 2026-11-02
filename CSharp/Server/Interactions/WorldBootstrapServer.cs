using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using PrimaryPackage.Core;
using PrimaryPackage.Networking;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionServer {
    private sealed class WorldBootstrapPeer {
        internal readonly WorldCatchup Cursor;
        internal Guid Flow => Cursor.Flow;
        internal int Part => Cursor.Part;
        internal ulong Cut => Cursor.Cut;
        internal bool Released => Cursor.Released;
        internal int Rate, ReplayIndex;
        internal WorldBootstrapPeer(Guid flow, int rate) { Cursor = new WorldCatchup(flow); Rate = rate; }
        internal Task<WorldBlob> Preparing;
        internal WorldBlob Blob;
        internal ChunkSender Sender;
        internal Guid Transfer;
        internal WorldReplayWindow Replay;
        internal Guid[] Targets;
        internal bool Accepted;
        internal double LastOffer, LastAck, WaitingApplyAt;
    }
    private readonly List<Session> bootstrapSessions = new();
    private Guid worldEpoch = Guid.NewGuid();
    private IDisposable worldReceiver;
    private readonly byte[] worldPacket = new byte[ChunkWire.MaxPacketBytes];
    private void AttachWorldBootstrap(Harmony harmony) {
        worldReceiver = ModNet.Listen(WorldContract.Channel, ReadWorldPacket);
        harmony.Patch(AccessTools.Method(typeof(GameServer), "ClientReadIngame"), prefix: ModuleHost.Bind(typeof(InteractionServer), nameof(NativeReadGate)));
        harmony.Patch(AccessTools.Method(typeof(GameServer), "ClientWriteIngame"), prefix: ModuleHost.Bind(typeof(InteractionServer), nameof(NativeWriteGate)));
        harmony.Patch(AccessTools.Method(typeof(ServerEntityEventManager), nameof(ServerEntityEventManager.Update)), prefix: ModuleHost.Bind(typeof(InteractionServer), nameof(NativeSyncProgress)));
    }
    private void ResetWorldBootstrap() { worldEpoch = Guid.NewGuid(); foreach (var session in sessions.Values) { session.Bootstrap = null; session.NativeSyncProgressAt = 0; } NativeWorld.Prune(); }
    private static bool NativeReadGate(IReadMessage inc) {
        if (instance?.live != true) { return true; }
        foreach (var session in instance.sessions.Values) { if (session.Client.Connection == inc.Sender) { return !RequiresBootstrap(session) || session.Bootstrap?.Released == true; } }
        if (instance.worldEdits.Head == 0) { return true; }
        foreach (Client client in GameMain.Server.ConnectedClients) {
            if (client.Connection == inc.Sender) { GameMain.Server.DisconnectClient(client, PeerDisconnectPacket.Custom("Для входа в изменённый мир требуется совместимая версия Primary Package. Согласуйте моды в лобби")); break; }
        }
        return false;
    }
    private static bool CanProjectWorld(Session session, DeviceLease lease) => session.WorldReady || session.Bootstrap?.Released == true && NativeWorld.IsConstructionEntity(lease.Item);
    private static bool RequiresBootstrap(Session session) => (session.Capabilities & WorldCapabilities.Baseline) != 0;
    private static bool NativeWriteGate(Client c) => instance?.live != true || !instance.sessions.TryGetValue(c.SessionId, out var session) || !RequiresBootstrap(session) || session.Bootstrap?.Released == true;
    private static void NativeSyncProgress() {
        if (instance?.live != true) { return; }
        foreach (var session in instance.sessions.Values) {
            Client client = session.Client;
            if (!client.InGame || !client.NeedsMidRoundSync || !RequiresBootstrap(session)) { session.NativeSyncProgressAt = 0; continue; }
            double now = Timing.TotalTime;
            if (session.NativeSyncProgressAt == 0 || client.LastRecvEntityEventID != session.NativeSyncProgress) { session.NativeSyncProgress = client.LastRecvEntityEventID; session.NativeSyncProgressAt = now; }
            // Extend only with actual native event progress. A connected but stuck client
            // still times out; changing global server/chat settings is unnecessary.
            client.MidRoundSyncTimeOut = Math.Max(client.MidRoundSyncTimeOut, session.NativeSyncProgressAt + 60);
        }
    }
    private WorldBaseline CaptureBaseline(ulong after, bool first) {
        ulong cut = worldEdits.Head;
        var items = new List<BaselineItem>(); int total = 0;
        foreach (var lease in devices.Values) {
            if (!lease.Current || !NativeWorld.IsConstructionEntity(lease.Item)) { continue; }
            var properties = new List<BaselineProperty>(); bool position = false; ulong positionRevision = 0;
            foreach (var edited in lease.Edited) {
                if (edited.Component == ushort.MaxValue) {
                    positionRevision = worldEdits.Revision(new(lease.Id, ushort.MaxValue, LiveItemTransform.PositionKey)); position = positionRevision > after; continue;
                }
                var target = lease.Item.Components[edited.Component]; string type = target.GetType().FullName;
                ulong revision = worldEdits.Revision(new(lease.Id, edited.Component, type + "|" + edited.Field));
                if (revision <= after) { continue; }
                string value = DeviceEditor.Resolve(target, edited.Field).Read(target);
                if (++total > WorldBaseline.MaxProperties || value.Length > 512) { throw new InvalidOperationException("World baseline property limit exceeded"); }
                properties.Add(new(edited.Component, type, edited.Field, value, revision));
            }
            if (!position && properties.Count == 0) { continue; }
            var local = LiveItemTransform.Decode(lease.PositionState);
            items.Add(new(lease.Id, lease.Item.ID, lease.Item.Prefab.Identifier.Value, lease.Item.Submarine?.ID ?? 0,
                position, local.Frame, local.Position.X, local.Position.Y, positionRevision, properties.ToArray()));
        }
        if (worldEdits.Head != cut) { throw new InvalidOperationException("World edit reentered a snapshot getter"); }
        return new(worldEpoch, cut, first ? NativeWorld.Capture() : Array.Empty<BaselineGraph>(), items.ToArray());
    }
    private void PrepareBaseline(Session session, WorldBootstrapPeer peer, ulong after) {
        WorldBaseline snapshot = CaptureBaseline(after, peer.Part == 0);
        peer.Cursor.Captured(snapshot.Cut); peer.Targets = new Guid[snapshot.Items.Length];
        for (int i = 0; i < peer.Targets.Length; i++) { peer.Targets[i] = snapshot.Items[i].Incarnation; }
        peer.Blob = null; peer.Sender = null; peer.Accepted = false; peer.WaitingApplyAt = 0;
        peer.Preparing = Task.Run(() => new WorldBlob(Guid.NewGuid(), snapshot.Encode()));
    }
    private void ReadWorldPacket(IReadMessage message, Client client) {
        if (!live || message.LengthBytes > WorldContract.MaxEnvelopeBytes || !sessions.TryGetValue(client.SessionId, out var session) || session.Client != client || !RequiresBootstrap(session)) { return; }
        double receivedAt = Now;
        session.WorldIngressBytes = Math.Min(4096, session.WorldIngressBytes + Math.Max(0, receivedAt - session.WorldIngressAt) * 2048); session.WorldIngressAt = receivedAt;
        if (message.LengthBytes > session.WorldIngressBytes) { return; } session.WorldIngressBytes -= message.LengthBytes;
        try {
            if (!ChunkWire.TryOpen(NativeWire.Read(message), out var reader, out var kind, out Guid transfer)) { return; }
            double now = Now;
            if (kind == ChunkPacket.Accept) {
                Guid flow = reader.Guid(), epoch = reader.Guid(); uint schema = reader.UInt32(), rate = reader.UInt32(); reader.Finish();
                if (schema != WorldContract.SchemaVersion || rate is < 2048 or > 262144) { throw new ArgumentException("Несовместимый протокол восстановления мира"); }
                if (GameMain.GameSession?.IsRunning != true) { return; }
                if (epoch != worldEpoch) { SendSnapshot(session); return; }
                var peer = session.Bootstrap;
                if (peer == null) {
                    if (transfer != flow || flow == Guid.Empty) { throw new ArgumentException("Invalid bootstrap request"); }
                    peer = session.Bootstrap = new WorldBootstrapPeer(flow, (int)rate);
                    PrepareBaseline(session, peer, 0);
                } else if (peer.Flow != flow) { return; }
                else if (peer.Released) { SendBootstrapComplete(session, peer); }
                else if (peer.Blob?.Id == transfer) { peer.Accepted = true; }
                return;
            }
            var active = session.Bootstrap;
            if (active == null || active.Transfer != transfer) { return; }
            if (kind == ChunkPacket.Ack) {
                int prefix = checked((int)reader.UInt32()); ulong following = reader.Bits(64); reader.Finish();
                if (now - active.LastAck < .02) { return; } active.LastAck = now;
                active.Sender?.Acknowledge(prefix, following, now);
            } else if (kind == ChunkPacket.Complete) {
                Guid flow = reader.Guid(); uint part = reader.UInt32(); ulong cut = reader.UInt(); reader.Finish();
                if (flow != active.Flow || part != active.Part || cut != active.Cut || !active.Cursor.Applied && active.Sender?.Complete != true) { return; }
                CatchupResult decision = active.Cursor.Confirm(flow, (int)part, cut, worldEdits.Head);
                if (decision == CatchupResult.Ignore) { return; }
                foreach (Guid token in active.Targets) { if (deviceLifetimes.TryGetValue(token, out var lease) && lease.Current) { session.DeviceBindings.Add(token); } }
                active.Targets = Array.Empty<Guid>();
                if (decision == CatchupResult.Tail) {
                    active.Replay = new WorldReplayWindow(cut, active.Rate, now);
                    active.Blob = null; active.Sender = null; // retain the transfer identity, not the snapshot payload per released peer
                    active.ReplayIndex = 0;
                    while (active.ReplayIndex < worldEdits.Changes.Count && worldEdits.Changes[active.ReplayIndex].Sequence <= cut) { active.ReplayIndex++; }
                } else { SendBootstrapComplete(session, active); }
            } else if (kind == ChunkPacket.Cancel) {
                string reason = reader.Text(1024); reader.Finish(); throw new InvalidOperationException("Клиент отклонил снимок мира: " + reason);
            } else { throw new ArgumentException("Unexpected client snapshot packet"); }
        } catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException) { FailBootstrap(session, error.Message); }
    }
    private void PumpWorldBootstrap(double now) {
        bootstrapSessions.Clear(); bootstrapSessions.AddRange(sessions.Values);
        foreach (var session in bootstrapSessions) {
            var peer = session.Bootstrap; if (peer == null || peer.Released) { continue; }
            if (peer.Preparing is { IsCompleted: true }) {
                if (peer.Preparing.IsFaulted) { FailBootstrap(session, peer.Preparing.Exception?.GetBaseException().Message ?? "Snapshot preparation failed"); continue; }
                peer.Blob = peer.Preparing.GetAwaiter().GetResult(); peer.Transfer = peer.Blob.Id; peer.Preparing = null;
                peer.Sender = new ChunkSender(peer.Blob, peer.Rate, now); peer.LastOffer = double.NegativeInfinity;
            }
            if (peer.Cursor.Applied) {
                if (peer.Replay.Expired(now)) { FailBootstrap(session, "Клиент не подтверждает применение изменений мира 60 секунд"); continue; }
                PumpBootstrapReplay(session, peer, now); continue;
            }
            if (peer.Blob == null) { continue; }
            if (peer.Sender.Complete) {
                if (peer.WaitingApplyAt == 0) { peer.WaitingApplyAt = now; }
                if (now - peer.WaitingApplyAt >= 60) { FailBootstrap(session, "Клиент не подтвердил применение полученного снимка за 60 секунд"); }
                continue;
            }
            if (peer.Sender.Expired(now)) { FailBootstrap(session, "Передача мира не продвигается 60 секунд. Можно переподключиться для нового снимка"); continue; }
            if (!peer.Accepted) {
                if (now - peer.LastOffer < peer.Sender.RetransmitTimeout) { continue; } peer.LastOffer = now;
                var writer = ChunkWire.Start(worldPacket, ChunkPacket.Offer, peer.Blob.Id); writer.Guid(peer.Flow); writer.Guid(worldEpoch); writer.UInt((uint)peer.Part); writer.UInt(peer.Cut); writer.UInt((uint)peer.Blob.Length); writer.Bytes(peer.Blob.Digest);
                SendWorldPacket(session.Client, ChunkWire.Finish(ref writer, worldPacket)); continue;
            }
            for (int budget = 0; budget < 4 && peer.Sender.TryNext(now, out int index, out var bytes); budget++) {
                var writer = ChunkWire.Start(worldPacket, ChunkPacket.Data, peer.Blob.Id); writer.UInt((uint)index); writer.Bytes(bytes.Span);
                if (!SendWorldPacket(session.Client, ChunkWire.Finish(ref writer, worldPacket))) { break; }
            }
        }
    }
    private bool SendWorldPacket(Client client, int length) => ModNet.Send(WorldContract.Channel, client.Connection, output => NativeWire.Write(output, worldPacket, length), DeliveryMethod.Unreliable);
    private void PumpBootstrapReplay(Session session, WorldBootstrapPeer peer, double now) {
        // Replay complete journal operations, including group inverses, in server order.
        // Unlike repeatedly recapturing snapshots, this can hand off during continuous edits.
        for (int budget = 0; budget < 8 && peer.ReplayIndex < worldEdits.Changes.Count; budget++) {
            int first = peer.ReplayIndex, end = first + 1; ulong sequence = worldEdits.Changes[first].Sequence;
            while (end < worldEdits.Changes.Count && worldEdits.Changes[end].Sequence == sequence) { end++; }
            int size = 128; bool positionHeader = false;
            for (int i = first; i < end; i++) {
                var change = worldEdits.Changes[i];
                if (!deviceLifetimes.TryGetValue(change.Key.Incarnation, out var lease) || !lease.Current || !NativeWorld.IsConstructionEntity(lease.Item)) { continue; }
                if (change.Key.Component == ushort.MaxValue && change.Key.Property == LiveItemTransform.PositionKey) {
                    if (!positionHeader) { size += 128; positionHeader = true; }
                    size += 16 + 16 + 5 + 5; // two GUIDs and the maximum two signed coordinate varints
                } else {
                    size = checked(size + 128 + WireEncoding.Utf8.GetByteCount(change.Key.Property) + WireEncoding.Utf8.GetByteCount(change.After));
                }
            }
            // Conservative UTF-8/envelope budget: no operation is split to fit the window.
            if (size > WorldReplayWindow.ByteCapacity) { FailBootstrap(session, "Операция мира превышает ограничение очереди синхронизации"); return; }
            if (!peer.Replay.CanSend(size, now)) { return; }
            var positions = new List<TranslationWire.StateRow>(end - first);
            for (int i = first; i < end; i++) {
                var change = worldEdits.Changes[i];
                if (!deviceLifetimes.TryGetValue(change.Key.Incarnation, out var lease) || !lease.Current || !NativeWorld.IsConstructionEntity(lease.Item)) { continue; }
                if (change.Key.Component == ushort.MaxValue && change.Key.Property == LiveItemTransform.PositionKey) { positions.Add(new(lease.Id, lease.Item.Submarine.ID, change.After)); }
            }
            if (positions.Count > 0) {
                var rows = positions.ToArray();
                if (!ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => {
                    InteractionProtocol.Header(output, InteractionPacket.WorldTransformBatch); TranslationWire.WriteState(output, worldEdits.Changes[first].Request, sequence, rows);
                })) { return; }
            }
            for (int i = first; i < end; i++) {
                var change = worldEdits.Changes[i];
                if (change.Key.Component == ushort.MaxValue || !deviceLifetimes.TryGetValue(change.Key.Incarnation, out var lease) || !lease.Current || !NativeWorld.IsConstructionEntity(lease.Item)) { continue; }
                int split = change.Key.Property.IndexOf('|'); if (split < 1) { throw new InvalidOperationException("Invalid journal property address"); }
                var component = DeviceEditor.Resolve(lease.Item, change.Key.Component, change.Key.Property[..split]);
                var field = DeviceEditor.Resolve(component, change.Key.Property[(split + 1)..]); ulong sample = ++worldSample;
                if (!ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => {
                    InteractionProtocol.Header(output, InteractionPacket.DeviceDelta); output.WriteString(lease.Id.ToString("N")); output.WriteBoolean(true);
                    output.WriteUInt16(change.Key.Component); output.WriteString(component.GetType().FullName); output.WriteString(field.Name); output.WriteByte((byte)field.Kind);
                    output.WriteUInt64(sequence); output.WriteUInt64(sample); output.WriteString(change.After);
                })) { return; }
            }
            if (!ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => {
                InteractionProtocol.Header(output, InteractionPacket.WorldBootstrapProgress); output.WriteString(peer.Flow.ToString("N"));
                output.WriteString(worldEpoch.ToString("N")); output.WriteUInt64(sequence);
            })) { return; }
            peer.Replay.Enqueue(sequence, size, now); peer.ReplayIndex = end; peer.Cursor.Advance(sequence);
        }
        if (peer.ReplayIndex == worldEdits.Changes.Count && peer.Cursor.CanRelease(worldEdits.Head)) {
            // Same reliable ordered channel as the tail. The client cannot start native
            // sync until every preceding tail operation has actually been interpreted.
            ulong head = worldEdits.Head;
            if (ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => {
                InteractionProtocol.Header(output, InteractionPacket.WorldBootstrapRelease); output.WriteString(peer.Flow.ToString("N")); output.WriteString(worldEpoch.ToString("N")); output.WriteUInt64(head);
            })) { peer.Cursor.Release(head); peer.Replay = null; }
        }
    }
    private void AcknowledgeBootstrapReplay(IReadMessage input, Session session, double now) {
        Guid flow = Guid.ParseExact(input.ReadString(), "N"), epoch = Guid.ParseExact(input.ReadString(), "N"); ulong sequence = input.ReadUInt64();
        var peer = session.Bootstrap;
        if (input.BitPosition > input.LengthBits || peer == null || flow != peer.Flow || epoch != worldEpoch || peer.Released || peer.Replay == null) { return; }
        peer.Replay.Acknowledge(sequence, now);
    }
    private void SendBootstrapComplete(Session session, WorldBootstrapPeer peer) {
        ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => {
            InteractionProtocol.Header(output, InteractionPacket.WorldBootstrapRelease); output.WriteString(peer.Flow.ToString("N"));
            output.WriteString(worldEpoch.ToString("N")); output.WriteUInt64(peer.Cursor.Replayed);
        });
    }
    private bool IsWorldBound(Session session, DeviceLease lease) {
        if (session.DeviceBindings.Contains(lease.Id)) { return true; }
        if (session.Bootstrap?.Released == true && NativeWorld.IsConstructionEntity(lease.Item) && lease.Id == BaseWorldIdentity.For(worldEpoch, lease.Item.ID)) { session.DeviceBindings.Add(lease.Id); return true; }
        return false;
    }
    private void FailBootstrap(Session session, string reason) {
        session.Bootstrap = null; ModLog.Error("World bootstrap " + session.Account + ": " + reason);
        GameMain.Server.DisconnectClient(session.Client, PeerDisconnectPacket.Custom("Не удалось восстановить структуру мира: " + reason));
    }
}
