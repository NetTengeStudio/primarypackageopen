using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using PrimaryPackage.GUI;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionClient {
    internal readonly record struct DeviceRow(ushort Component, string Type, string Field, DeviceValueKind Kind, ulong Revision, string Value, ulong Sample);
    internal readonly record struct EditHistoryRow(Guid Request, string Actor, Guid Target, string Property, ulong Revision, long Timestamp, bool Undone, bool Own, byte Changes);
    private readonly record struct DeviceOffer(ushort Id, string Prefab);
    internal readonly List<EditHistoryRow> EditHistory = new();
    internal event Action HistoryReceived;
    internal event Action<ushort, Guid, DeviceRow> DeviceValueReceived;
    private readonly Dictionary<(Guid Token, ushort Component, string Property), DeviceRow> deviceValues = new();
    internal event Action<ushort, Guid, List<DeviceRow>> DeviceReceived;
    private readonly Dictionary<Guid, Item> deviceBindings = new();
    private readonly Dictionary<Guid, DeviceOffer> deviceOffers = new();
    private readonly Queue<Guid> deviceOfferQueue = new();
    private readonly List<Guid> deviceBindAcks = new(16);
    private double nextDeviceBindAck;
    private readonly Dictionary<Guid, List<DeviceRow>> deviceSnapshots = new();
    private bool worldReadySent;
    private void ResetDevices() { StopLiveEditor(); views?.CloseLiveEditor(); transformStates.Clear(); deviceValues.Clear(); deviceBindings.Clear(); deviceOffers.Clear(); deviceOfferQueue.Clear(); deviceBindAcks.Clear(); nextDeviceBindAck = 0; deviceSnapshots.Clear(); EditHistory.Clear(); worldReadySent = false; }
    internal void InspectDevice(Item item) {
        if (!Allows(InteractionAction.ManageEntities)) { return; }
        if (Multiplayer) { Send(InteractionPacket.DeviceInspect, output => output.WriteUInt16(item.ID)); return; }
        var rows = new List<DeviceRow>();
        for (int c = 0; c < item.Components.Count && c < ushort.MaxValue; c++) {
            var target = item.Components[c]; foreach (var field in DeviceEditor.Schema(target)) { rows.Add(new((ushort)c, target.GetType().FullName, field.Name, field.Kind, 0, field.Read(target), 0)); }
        }
        DeviceReceived?.Invoke(item.ID, Guid.Empty, rows);
    }
    internal void ChangeDevice(Item item, Guid token, DeviceRow row, string value) {
        if (!Allows(InteractionAction.ManageEntities)) { throw new UnauthorizedAccessException("Нет разрешения редактировать объекты"); }
        var target = DeviceEditor.Resolve(item, row.Component, row.Type); DeviceEditor.Field field = DeviceEditor.Resolve(target, row.Field);
        field.Parse(value);
        if (!Multiplayer) {
            if (field.Read(target) != row.Value) { throw new InvalidOperationException("Настройка уже изменилась"); }
            field.Write(target, value); InspectDevice(item); return;
        }
        if (!deviceBindings.TryGetValue(token, out var bound) || bound != item || item.Removed) { throw new InvalidOperationException("Привязка объекта устарела"); }
        Send(InteractionPacket.DeviceChange, output => {
            output.WriteString(Guid.NewGuid().ToString("N")); output.WriteString(token.ToString("N")); output.WriteUInt16(row.Component);
            output.WriteString(row.Type); output.WriteString(row.Field); output.WriteUInt64(row.Revision); output.WriteString(row.Value); output.WriteString(value);
        });
    }
    private static DeviceRow ReadDeviceRow(IReadMessage input, Item item) {
        ushort component = input.ReadUInt16(); string type = input.ReadString(), field = input.ReadString(); var kind = (DeviceValueKind)input.ReadByte();
        ulong revision = input.ReadUInt64(), sample = input.ReadUInt64(); string value = input.ReadString();
        if (type.Length > 160 || field.Length > 128 || value.Length > 512) { throw new ArgumentException("Слишком длинное свойство мира"); }
        var target = DeviceEditor.Resolve(item, component, type); var descriptor = DeviceEditor.Resolve(target, field);
        if (descriptor.Kind != kind) { throw new ArgumentException("Схема компонента отличается от серверной"); }
        return new(component, type, field, kind, revision, value, sample);
    }
    private bool ResolveWorldItem(Guid token, out Item item) {
        if (deviceBindings.TryGetValue(token, out item)) { return !item.Removed && Entity.FindEntityByID(item.ID) == item; }
        if (!BaseWorldIdentity.TryResolve(bootstrapEpoch, token, out ushort id) || Entity.FindEntityByID(id) is not Item { Removed: false, FullyInitialized: true } original || !NativeWorld.IsConstructionEntity(original)) { item = null; return false; }
        deviceBindings.Add(token, original); item = original; return true;
    }
    private void ReadDeviceBind(IReadMessage input) {
        ushort id = input.ReadUInt16(); Guid token = Guid.ParseExact(input.ReadString(), "N"); string prefab = input.ReadString();
        if (token == Guid.Empty || prefab.Length > 128 || deviceOffers.Count >= 1024) { throw new ArgumentException("Неверная привязка объекта"); }
        if (!deviceOffers.ContainsKey(token)) { deviceOfferQueue.Enqueue(token); }
        deviceOffers[token] = new(id, prefab);
    }
    private void ReadDevice(IReadMessage input) {
        Guid token = Guid.ParseExact(input.ReadString(), "N"); bool first = input.ReadBoolean(), last = input.ReadBoolean(); int count = input.ReadByte();
        if (!ResolveWorldItem(token, out Item item) || item.Removed || Entity.FindEntityByID(item.ID) != item) { return; }
        if (count > 8) { throw new ArgumentException("Слишком большой пакет свойств"); }
        if (first) { deviceSnapshots[token] = new(); }
        if (!deviceSnapshots.TryGetValue(token, out var rows)) { throw new InvalidOperationException("Нет начала снимка объекта"); }
        if (rows.Count + count > 512) { throw new ArgumentException("Слишком много свойств"); }
        for (int i = 0; i < count; i++) {
            DeviceRow row = ReadDeviceRow(input, item); var key = (token, row.Component, row.Field);
            if (deviceValues.TryGetValue(key, out var current) && current.Sample > row.Sample) { row = current; }
            if (row.Revision > 0) { deviceValues[key] = row; } rows.Add(row);
        }
        if (last) { deviceSnapshots.Remove(token); DeviceReceived?.Invoke(item.ID, token, rows); }
    }
    private void ReadDeviceDelta(IReadMessage input) {
        Guid token = Guid.ParseExact(input.ReadString(), "N");
        if (!ResolveWorldItem(token, out Item item) || item.Removed || Entity.FindEntityByID(item.ID) != item) { return; }
        bool apply = input.ReadBoolean(); DeviceRow row = ReadDeviceRow(input, item); var key = (token, row.Component, row.Field);
        if (deviceValues.TryGetValue(key, out var previous) && (previous.Sample >= row.Sample || apply && previous.Revision >= row.Revision)) { return; }
        if (apply) { DeviceEditor.Resolve(item.Components[row.Component], row.Field).Write(item.Components[row.Component], row.Value); }
        if (row.Revision > 0) { deviceValues[key] = row; } DeviceValueReceived?.Invoke(item.ID, token, row);
    }
    private void ApplyDeviceDeltas() {
        if (!Multiplayer || !Connected || GameMain.GameSession?.IsRunning != true || !bootstrapReleased) { return; }
        if (!GameMain.Client.MidRoundSyncing && !worldReadySent) { worldReadySent = Send(InteractionPacket.WorldReady, null); }
        if (deviceOffers.Count == 0 || GUIRuntime.Now < nextDeviceBindAck) { return; }
        nextDeviceBindAck = GUIRuntime.Now + .125; deviceBindAcks.Clear();
        int count = Math.Min(32, deviceOfferQueue.Count);
        for (int i = 0; i < count && deviceBindAcks.Count < 16; i++) {
            Guid token = deviceOfferQueue.Dequeue();
            if (!deviceOffers.TryGetValue(token, out DeviceOffer offer)) { continue; }
            if (Entity.FindEntityByID(offer.Id) is not Item { Removed: false, FullyInitialized: true } item) { deviceOfferQueue.Enqueue(token); continue; }
            if (item.Prefab.Identifier.Value != offer.Prefab) { deviceOffers.Remove(token); continue; }
            deviceBindings[token] = item; deviceBindAcks.Add(token);
        }
        if (deviceBindAcks.Count == 0) { return; }
        bool sent = Send(InteractionPacket.DeviceBindAck, output => { output.WriteByte((byte)deviceBindAcks.Count); foreach (Guid token in deviceBindAcks) { output.WriteString(token.ToString("N")); } });
        foreach (Guid token in deviceBindAcks) { if (sent) { deviceOffers.Remove(token); } else { deviceOfferQueue.Enqueue(token); } }
    }
    private void ForgetDevice(Guid token) {
        if (deviceBindings.Remove(token, out Item item)) { transformStates.Remove(item); }
        deviceOffers.Remove(token); deviceSnapshots.Remove(token);
        // Values are bounded by the server journal and released with the round; no ID-based rebinding.
    }
    internal void StopDeviceInspection() { if (Multiplayer) { Send(InteractionPacket.DeviceUninspect, null); } }
    internal void ExportWorldEdits() { if (Allows(InteractionAction.ManageEntities) && Multiplayer) { Send(InteractionPacket.WorldExport, null); } }
    internal void RequestEditHistory() { if (Allows(InteractionAction.ManageEntities) && Multiplayer) { Send(InteractionPacket.EditHistory, null); } }
    internal void ReverseEdit(EditHistoryRow row) {
        if (!row.Own || !Allows(InteractionAction.ManageEntities)) { return; }
        Send(InteractionPacket.EditReverse, output => { output.WriteString(row.Request.ToString("N")); output.WriteUInt64(row.Revision); output.WriteBoolean(row.Undone); });
    }
    private void ReadEditHistory(IReadMessage input) {
        int count = input.ReadByte(); if (count > 16) { throw new ArgumentException("История слишком велика"); }
        var rows = new List<EditHistoryRow>(count);
        for (int i = 0; i < count; i++) {
            Guid request = Guid.ParseExact(input.ReadString(), "N"); string actor = input.ReadString(); Guid target = Guid.ParseExact(input.ReadString(), "N"); string property = input.ReadString();
            if (actor.Length > 128 || property.Length > WorldEditJournal.MaxPropertyAddressLength) { throw new ArgumentException("Некорректная история"); }
            ulong revision = input.ReadUInt64(); long timestamp = input.ReadInt64(); bool undone = input.ReadBoolean(), own = input.ReadBoolean(); byte changes = input.ReadByte();
            if (changes is < 1 or > WorldEditJournal.MaxBatch) { throw new ArgumentException("Некорректное число изменений в истории"); }
            rows.Add(new(request, actor, target, property, revision, timestamp, undone, own, changes));
        }
        EditHistory.Clear(); EditHistory.AddRange(rows); HistoryReceived?.Invoke();
    }
}
