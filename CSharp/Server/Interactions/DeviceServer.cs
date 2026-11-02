using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionServer {
    private sealed class DeviceLease : IWorldEditTarget {
        internal readonly Guid Id;
        internal Item Item;
        internal DeviceLease(Item item, Guid id) { Item = item; Id = id; }
        private Submarine transformFrame;
        private Guid transformFrameId;
        internal string PositionState {
            get {
                if (transformFrameId == Guid.Empty || !ReferenceEquals(transformFrame, Item.Submarine)) { transformFrame = Item.Submarine; transformFrameId = Guid.NewGuid(); }
                return LiveItemTransform.Encode(transformFrameId, new Microsoft.Xna.Framework.Point(Item.Rect.X, Item.Rect.Y));
            }
        }
        private string PositionValue(string state) {
            var desired = LiveItemTransform.Decode(state); var current = LiveItemTransform.Decode(PositionState);
            if (desired.Frame != current.Frame) { throw new InvalidOperationException("Изменилась подлодка объекта. Обновите систему координат"); }
            return desired.Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + desired.Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        internal readonly HashSet<(ushort Component, string Field)> Edited = new();
        internal bool Current => Item is { Removed: false } && Entity.FindEntityByID(Item.ID) == Item;
        private ItemComponentTarget Target(in WorldEditKey key) {
            if (!Current || key.Incarnation != Id || key.Component >= Item.Components.Count) { throw new InvalidOperationException("Цель удалена или заменена"); }
            int split = key.Property.IndexOf('|');
            if (split < 1) { throw new ArgumentException("Неверный адрес свойства"); }
            var target = DeviceEditor.Resolve(Item, key.Component, key.Property[..split]);
            return new(target, DeviceEditor.Resolve(target, key.Property[(split + 1)..]));
        }
        private static bool IsPosition(in WorldEditKey key) => key.Component == ushort.MaxValue && key.Property == LiveItemTransform.PositionKey;
        public string Read(in WorldEditKey key) {
            if (IsPosition(key)) {
                if (!Current) { throw new InvalidOperationException("Цель удалена"); }
                return PositionState;
            }
            var target = Target(key); return target.Field.Read(target.Component);
        }
        public void Validate(in WorldEditKey key, string value) {
            if (IsPosition(key)) {
                if (!Current) { throw new InvalidOperationException("Цель удалена"); }
                if (PositionState == value) { return; } // unchanged alignment anchor: read dependency, no physical mutation
                LiveItemTransform.Validate(Item, PositionValue(value), true); return;
            }
            Target(key).Field.Parse(value);
        }
        public void Write(in WorldEditKey key, string value) {
            if (!IsPosition(key)) { var target = Target(key); target.Field.Write(target.Component, value); return; }
            LiveItemTransform.Write(Item, PositionValue(value));
        }
    }
    private readonly record struct DeviceSnapshotValue(ushort Component, string Type, DeviceEditor.Field Field, ulong Revision, ulong Sample, string Value);
    private readonly record struct ItemComponentTarget(Barotrauma.Items.Components.ItemComponent Component, DeviceEditor.Field Field);
    private sealed class EditorWorld : IWorldEditTarget {
        private readonly InteractionServer owner;
        internal EditorWorld(InteractionServer owner) { this.owner = owner; }
        private DeviceLease Resolve(in WorldEditKey key) => owner.deviceLifetimes.TryGetValue(key.Incarnation, out var lease) && lease.Current
            ? lease : throw new InvalidOperationException("Цель транзакции удалена или заменена");
        public string Read(in WorldEditKey key) => Resolve(key).Read(key);
        public void Validate(in WorldEditKey key, string value) => Resolve(key).Validate(key, value);
        public void Write(in WorldEditKey key, string value) => Resolve(key).Write(key, value);
    }
    private EditorWorld editorWorld;
    private EditorWorld WorldTarget => editorWorld ??= new EditorWorld(this);
    private readonly Dictionary<ushort, DeviceLease> devices = new();
    private readonly Dictionary<Guid, DeviceLease> deviceLifetimes = new();
    private readonly WorldEditJournal worldEdits = new();
    private readonly List<ushort> removedDevices = new();
    private double nextDeviceObserve;
    private ulong worldSample;
    private void ClearDevices() {
        devices.Clear(); deviceLifetimes.Clear(); worldEdits.Clear(); worldSample = 0;
        translationSessions.Clear(); translationCursor = 0; sentTransformSnapshots.Clear();
        foreach (Session session in sessions.Values) { session.WorldReady = session.WorldReadyRequested = false; session.Translation = null; session.TranslationOutbox.Clear(); session.PendingTransformSnapshots.Clear(); session.TransformSnapshots.Clear(); session.DeviceBindings.Clear(); session.DeviceOffers.Clear(); session.PendingDeviceOffers.Clear(); session.DeviceReplay.Clear(); session.DeviceCursor = 0; session.InspectedDevice = Guid.Empty; session.DeviceObserved.Clear(); session.DeviceSchema.Clear(); session.DeviceObserveCursor = 0; session.DeviceSnapshot.Clear(); session.DeviceSnapshotLease = null; session.DeviceSnapshotCursor = 0; }
    }
    private void PruneDevices() {
        removedDevices.Clear();
        foreach (var pair in devices) { if (!pair.Value.Current) { removedDevices.Add(pair.Key); deviceLifetimes.Remove(pair.Value.Id); } }
        foreach (ushort id in removedDevices) {
            Guid token = devices[id].Id;
            foreach (Session session in sessions.Values) {
                session.DeviceBindings.Remove(token); session.PendingDeviceOffers.Remove(token);
                ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => { InteractionProtocol.Header(output, InteractionPacket.DeviceForget); output.WriteString(token.ToString("N")); });
            }
            devices.Remove(id);
        }
    }
    private DeviceLease Device(Item item) {
        if (devices.TryGetValue(item.ID, out var lease) && lease.Item == item) { return lease; }
        PruneDevices();
        if (devices.Count >= 1024) { throw new InvalidOperationException("Достигнут предел 1024 редактируемых объектов"); }
        devices[item.ID] = lease = new DeviceLease(item, NativeWorld.IsConstructionEntity(item) ? BaseWorldIdentity.For(worldEpoch, item.ID) : Guid.NewGuid()); deviceLifetimes.Add(lease.Id, lease); return lease;
    }
    private void InspectDevice(Client client, ushort id) {
        if (!client.InGame || client.NeedsMidRoundSync || GameMain.GameSession?.IsRunning != true) { throw new InvalidOperationException("Дождитесь завершения синхронизации мира"); }
        if (Entity.FindEntityByID(id) is not Item { Removed: false } item) { throw new ArgumentException("Предмет больше не существует"); }
        Session session = sessions[client.SessionId]; DeviceLease lease = Device(item);
        session.InspectedDevice = lease.Id;
        if (session.DeviceBindings.Contains(lease.Id)) { SendDeviceSnapshot(session, lease); }
        else { OfferDevice(session, lease); }
    }
    private void OfferDevice(Session session, DeviceLease lease) {
        if (lease.Current && CanProjectWorld(session, lease) && session.PendingDeviceOffers.Add(lease.Id)) { session.DeviceOffers.Enqueue(lease); }
    }
    private void PumpDeviceOffers(Session session) {
        for (int i = 0; i < 8 && session.DeviceOffers.TryDequeue(out var lease); i++) {
            if (!lease.Current) { session.PendingDeviceOffers.Remove(lease.Id); continue; }
            bool sent = ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => {
                InteractionProtocol.Header(output, InteractionPacket.DeviceBind); output.WriteUInt16(lease.Item.ID);
                output.WriteString(lease.Id.ToString("N")); output.WriteString(lease.Item.Prefab.Identifier.Value);
            });
            if (!sent) { session.DeviceOffers.Enqueue(lease); break; }
        }
    }
    private void BindDevice(IReadMessage input, Session session) {
        int count = input.ReadByte(); if (count is < 1 or > 16) { throw new ArgumentException("Недопустимое число подтверждений объектов"); }
        for (int i = 0; i < count; i++) {
            Guid token = Guid.ParseExact(input.ReadString(), "N");
            if (!deviceLifetimes.TryGetValue(token, out DeviceLease lease) || !lease.Current || !CanProjectWorld(session, lease)) { continue; }
            session.PendingDeviceOffers.Remove(token);
            if (session.DeviceBindings.Add(token)) {
                foreach (var field in lease.Edited) { session.DeviceReplay.Add((lease, field.Component, field.Field)); }
            }
            bool transformRequested = session.TransformSnapshots.Remove(token);
            if (session.InspectedDevice == token) { SendDeviceSnapshot(session, lease); }
            else if (transformRequested) { SendTransformSnapshot(session, lease, false); }
        }
    }
    private void SendDeviceSnapshot(Session session, DeviceLease lease) {
        session.DeviceObserved.Clear(); session.DeviceSchema.Clear(); session.DeviceObserveCursor = 0;
        session.DeviceSnapshot.Clear(); session.DeviceSnapshotLease = null; session.DeviceSnapshotCursor = 0;
        try {
        for (int c = 0; c < lease.Item.Components.Count && c < ushort.MaxValue; c++) {
            var component = lease.Item.Components[c];
            foreach (var field in DeviceEditor.Schema(component)) {
                if (session.DeviceSnapshot.Count == 512) { session.DeviceSnapshot.Clear(); session.DeviceSnapshotLease = null; throw new InvalidOperationException("Превышен предел 512 свойств объекта"); }
                string value = field.Read(component); if (value.Length > 512) { throw new InvalidOperationException("Текущее значение " + field.Name + " превышает сетевой предел 512 символов"); }
                var key = new WorldEditKey(lease.Id, (ushort)c, component.GetType().FullName + "|" + field.Name); ulong revision = worldEdits.Revision(key);
                session.DeviceSchema.Add(((ushort)c, field)); session.DeviceObserved[((ushort)c, field.Name)] = (value, revision);
                session.DeviceSnapshot.Add(new((ushort)c, component.GetType().FullName, field, revision, ++worldSample, value));
            }
        }
        } catch {
            session.InspectedDevice = Guid.Empty; session.DeviceSnapshot.Clear(); session.DeviceSchema.Clear(); session.DeviceObserved.Clear(); throw;
        }
        session.DeviceSnapshotLease = lease;
        SendTransformSnapshot(session, lease, false);
    }
    private void PumpDeviceSnapshot(Session session) {
        DeviceLease lease = session.DeviceSnapshotLease;
        if (lease == null) { return; }
        if (!lease.Current) { session.DeviceSnapshot.Clear(); session.DeviceSnapshotLease = null; return; }
        int first = session.DeviceSnapshotCursor, count = Math.Min(8, session.DeviceSnapshot.Count - first);
        bool last = first + count == session.DeviceSnapshot.Count;
        bool sent = ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => {
            InteractionProtocol.Header(output, InteractionPacket.DeviceSnapshot); output.WriteString(lease.Id.ToString("N"));
            output.WriteBoolean(first == 0); output.WriteBoolean(last); output.WriteByte((byte)count);
            for (int i = first; i < first + count; i++) {
                var row = session.DeviceSnapshot[i]; output.WriteUInt16(row.Component); output.WriteString(row.Type); output.WriteString(row.Field.Name);
                output.WriteByte((byte)row.Field.Kind); output.WriteUInt64(row.Revision); output.WriteUInt64(row.Sample); output.WriteString(row.Value);
            }
        });
        if (!sent) { return; }
        session.DeviceSnapshotCursor += count;
        if (last) { session.DeviceSnapshot.Clear(); session.DeviceSnapshotLease = null; }
    }
    private void ChangeDevice(IReadMessage input, Client client) {
        Session session = sessions[client.SessionId];
        Guid request = Guid.ParseExact(input.ReadString(), "N"), token = Guid.ParseExact(input.ReadString(), "N");
        ushort component = input.ReadUInt16(); string type = input.ReadString(), property = input.ReadString();
        ulong revision = input.ReadUInt64(); string expected = input.ReadString(), value = input.ReadString();
        if (GameMain.GameSession?.IsRunning != true || !session.WorldReady || client.NeedsMidRoundSync ||
            !session.DeviceBindings.Contains(token) || !deviceLifetimes.TryGetValue(token, out var lease) || !lease.Current || type.Length > 160 || property.Length > 128) {
            throw new ArgumentException("Объект удалён, заменён или не синхронизирован");
        }
        RequireEditingClients();
        var componentTarget = DeviceEditor.Resolve(lease.Item, component, type);
        value = DeviceEditor.Format(DeviceEditor.Resolve(componentTarget, property).Parse(value));
        var key = new WorldEditKey(token, component, type + "|" + property);
        try {
            var entry = worldEdits.Apply(request, session.Account, key, revision, expected, value, WorldTarget, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            lease.Edited.Add((component, property));
            PublishDevice(lease, component, property); SendEditHistory(session);
            ModLog.Message("GM world " + entry.Request + " " + session.Account + " " + token + " " + component + ":" + property + " rev=" + entry.Revision);
        } catch (InvalidOperationException) { SendDeviceSnapshot(session, lease); throw; }
    }
    private void PublishDevice(DeviceLease lease, ushort component, string field) {
        foreach (Session session in sessions.Values) {
            if (!CanProjectWorld(session, lease)) { continue; }
            if (IsWorldBound(session, lease)) { ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => WriteDeviceDelta(output, lease, component, field)); }
            else { OfferDevice(session, lease); }
        }
    }
    private void WriteDeviceRow(IWriteMessage output, DeviceLease lease, ushort component, string field) {
        var target = lease.Item.Components[component]; DeviceEditor.Field descriptor = DeviceEditor.Resolve(target, field);
        output.WriteUInt16(component); output.WriteString(target.GetType().FullName); output.WriteString(field); output.WriteByte((byte)descriptor.Kind);
        output.WriteUInt64(worldEdits.Revision(new(lease.Id, component, target.GetType().FullName + "|" + field))); output.WriteUInt64(++worldSample); output.WriteString(descriptor.Read(target));
    }
    private void WriteDeviceDelta(IWriteMessage output, DeviceLease lease, ushort component, string field, bool apply = true) {
        if (component == ushort.MaxValue && field == LiveItemTransform.PositionKey) { WriteTransform(output, lease, true); return; }
        InteractionProtocol.Header(output, InteractionPacket.DeviceDelta); output.WriteString(lease.Id.ToString("N")); output.WriteBoolean(apply); WriteDeviceRow(output, lease, component, field);
    }
    private void QueueDeviceReplay(Session session, double now) {
        session.WorldReadyRequested = true;
        if (!session.Client.InGame || session.Client.NeedsMidRoundSync || (session.Capabilities & WorldContract.Supported) != WorldContract.Supported) { return; }
        if (session.WorldReady) { return; }
        session.WorldReady = true;
        foreach (var lease in devices.Values) { if (lease.Current && lease.Edited.Count > 0) { OfferDevice(session, lease); } }
    }
    private void ObserveDevices(double now) {
        if (now < nextDeviceObserve) { return; } nextDeviceObserve = now + .5;
        foreach (Session session in sessions.Values) {
            if (!session.WorldReady || session.DeviceSnapshotLease != null || !permissions.Allows(session.Client, InteractionAction.ManageEntities)) { continue; }
            if (!deviceLifetimes.TryGetValue(session.InspectedDevice, out var lease) || !lease.Current || !session.DeviceBindings.Contains(lease.Id)) { continue; }
            int sent = 0;
            for (int scanned = 0; scanned < session.DeviceSchema.Count && sent < 8; scanned++) {
                if (session.DeviceObserveCursor >= session.DeviceSchema.Count) { session.DeviceObserveCursor = 0; session.DeviceSnapshot.Clear(); session.DeviceSnapshotLease = null; session.DeviceSnapshotCursor = 0; }
                var descriptor = session.DeviceSchema[session.DeviceObserveCursor++];
                if (descriptor.Component >= lease.Item.Components.Count) { session.InspectedDevice = Guid.Empty; break; }
                var component = lease.Item.Components[descriptor.Component]; var field = descriptor.Field;
                if (!field.Property.PropertyInfo.DeclaringType.IsInstanceOfType(component)) { session.InspectedDevice = Guid.Empty; break; }
                var key = (descriptor.Component, field.Name); string value = field.Read(component);
                ulong revision = worldEdits.Revision(new(lease.Id, descriptor.Component, component.GetType().FullName + "|" + field.Name));
                if (session.DeviceObserved.TryGetValue(key, out var previous) && previous.Value == value && previous.Revision == revision) { continue; }
                session.DeviceObserved[key] = (value, revision); sent++;
                ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => WriteDeviceDelta(output, lease, descriptor.Component, field.Name, false));
            }
        }
    }
    private void ReverseEdit(IReadMessage input, Session session) {
        Guid request = Guid.ParseExact(input.ReadString(), "N"); ulong revision = input.ReadUInt64(); bool redo = input.ReadBoolean();
        WorldEditJournal.Entry selected = null;
        foreach (var entry in worldEdits.Entries) { if (entry.Request == request) { selected = entry; break; } }
        if (!session.WorldReady || session.Client.NeedsMidRoundSync || selected == null || !deviceLifetimes.TryGetValue(selected.Key.Incarnation, out var lease) || !lease.Current) { throw new InvalidOperationException("Цель отмены удалена или недоступна"); }
        RequireEditingClients();
        if (IsTranslation(selected)) { QueueTranslationReverse(session, selected, revision, redo); return; }
        var reversed = worldEdits.Reverse(request, session.Account, revision, redo, WorldTarget, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var mutation in reversed.Mutations) {
            if (deviceLifetimes.TryGetValue(mutation.Key.Incarnation, out var changed) && changed.Current) {
                PublishDevice(changed, mutation.Key.Component, mutation.Key.Property[(mutation.Key.Property.IndexOf('|') + 1)..]);
            }
        }
        SendEditHistory(session);
    }
    private void WriteTransform(IWriteMessage output, DeviceLease lease, bool apply) {
        InteractionProtocol.Header(output, InteractionPacket.WorldTransform); output.WriteString(lease.Id.ToString("N")); output.WriteBoolean(apply);
        output.WriteUInt64(worldEdits.Revision(new(lease.Id, ushort.MaxValue, LiveItemTransform.PositionKey)));
        output.WriteUInt16(lease.Item.Submarine?.ID ?? 0); output.WriteString(lease.PositionState); output.WriteString(LiveItemTransform.Unavailable(lease.Item) ?? "");
    }
    private void SendTransformSnapshot(Session session, DeviceLease lease, bool apply) {
        session.PendingTransformSnapshots.TryGetValue(lease.Id, out bool previous);
        session.PendingTransformSnapshots[lease.Id] = previous || apply;
    }
    private void RequireEditingClients() {
        if (worldEdits.Faulted) { throw new InvalidOperationException("Редактор остановлен до следующего раунда после ошибки восстановления транзакции. Подробности в серверном журнале"); }
        foreach (Client client in GameMain.Server.ConnectedClients) {
            if (!client.InGame) { continue; }
            if (!sessions.TryGetValue(client.SessionId, out Session session) || session.Client != client ||
                (session.Capabilities & (WorldCapabilities.Properties | WorldCapabilities.StaticTranslation | WorldCapabilities.BatchTranslation)) != (WorldCapabilities.Properties | WorldCapabilities.StaticTranslation | WorldCapabilities.BatchTranslation)) {
                throw new InvalidOperationException("Игрок " + client.Name + " ещё не согласовал Live Editor v4; операция мира не выполнена");
            }
        }
    }
    public static bool AllowEditedWorldSync(Client client) {
        if (instance?.live != true || instance.worldEdits.Entries.Count == 0) { return true; }
        if (instance.sessions.TryGetValue(client.SessionId, out Session session) && session.Client == client &&
            (session.Capabilities & WorldContract.Supported) == WorldContract.Supported) { return true; }
        GameMain.Server.DisconnectClient(client, PeerDisconnectPacket.Custom("Для входа в отредактированный раунд требуется Primary Package с протоколом Live Editor v4. Завершите согласование модов в лобби и подключитесь снова."));
        return false;
    }
    private void SendEditHistory(Session session) {
        int first = Math.Max(0, worldEdits.Entries.Count - 16);
        ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => {
            InteractionProtocol.Header(output, InteractionPacket.EditHistory); output.WriteByte((byte)(worldEdits.Entries.Count - first));
            for (int i = first; i < worldEdits.Entries.Count; i++) {
                var entry = worldEdits.Entries[i]; output.WriteString(entry.Request.ToString("N")); output.WriteString(entry.Actor);
                output.WriteString(entry.Key.Incarnation.ToString("N")); output.WriteString(entry.Key.Property); output.WriteUInt64(entry.Revision);
                output.WriteInt64(entry.Timestamp); output.WriteBoolean(entry.State == WorldEditState.Undone); output.WriteBoolean(entry.Actor == session.Account); output.WriteByte((byte)entry.Mutations.Length);
            }
        });
    }
}
