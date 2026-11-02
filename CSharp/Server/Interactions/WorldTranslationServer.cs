using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed partial class InteractionServer {
    private const int TranslationOutboxCapacity = 32;
    private sealed record ConfirmedTranslation(Guid Request, ulong Revision, TranslationWire.StateRow[] Rows);
    private readonly List<Guid> sentTransformSnapshots = new(8);
    private readonly List<Session> translationSessions = new();
    private int translationCursor;
    private sealed record PendingTranslation(Guid Request, WorldMutation[] Mutations, double Deadline, ulong ReverseRevision = 0, bool Redo = false);
    private static bool IsTranslation(WorldEditJournal.Entry entry) {
        foreach (var mutation in entry.Mutations) { if (mutation.Key.Component != ushort.MaxValue || mutation.Key.Property != LiveItemTransform.PositionKey) { return false; } }
        return true;
    }
    private void RequireWorldReady(Session session) {
        if (!session.WorldReady || !session.Client.InGame || session.Client.NeedsMidRoundSync || GameMain.GameSession?.IsRunning != true) { throw new InvalidOperationException("Дождитесь завершения синхронизации мира"); }
    }
    private void InspectTransforms(IReadMessage input, Session session) {
        RequireWorldReady(session);
        int count = input.ReadByte();
        if (count is < 1 or > WorldEditJournal.MaxBatch) { throw new ArgumentException("Недопустимое выделение"); }
        // Decode the entire request before changing bindings or sending any response.
        var leases = new DeviceLease[count]; var ids = new HashSet<ushort>(count);
        for (int i = 0; i < count; i++) {
            ushort id = input.ReadUInt16();
            if (!ids.Add(id) || Entity.FindEntityByID(id) is not Item { Removed: false } item) { throw new ArgumentException("Объект выделения удалён или повторяется"); }
            leases[i] = Device(item);
        }
        if (input.BitPosition > input.LengthBits) { throw new ArgumentException("Оборвано выделение"); }
        session.TransformSnapshots.Clear();
        foreach (var lease in leases) {
            if (session.DeviceBindings.Contains(lease.Id)) { SendTransformSnapshot(session, lease, false); }
            else { session.TransformSnapshots.Add(lease.Id); OfferDevice(session, lease); }
        }
    }
    private void TranslateWorld(IReadMessage input, Session session) {
        RequireWorldReady(session); RequireEditingClients();
        var rows = TranslationWire.ReadIntent(input, out Guid request);
        if (session.Translation != null) {
            var pending = session.Translation;
            if (pending.Request != request || pending.ReverseRevision != 0 || pending.Mutations.Length != rows.Length) { throw new InvalidOperationException("Предыдущее перемещение ещё ожидает привязки клиентов"); }
            for (int i = 0; i < rows.Length; i++) {
                var old = pending.Mutations[i]; var row = rows[i];
                if (old.Key.Incarnation != row.Token || old.ExpectedRevision != row.Revision || old.Before != row.Before || old.After != row.After) { throw new InvalidOperationException("Повторный запрос отличается от ожидающего перемещения"); }
            }
            return;
        }
        var mutations = new WorldMutation[rows.Length];
        for (int i = 0; i < rows.Length; i++) {
            var row = rows[i];
            if (!session.DeviceBindings.Contains(row.Token)) { throw new InvalidOperationException("Привязка выделения устарела"); }
            mutations[i] = new(new(row.Token, ushort.MaxValue, LiveItemTransform.PositionKey), row.Revision, row.Before, row.After);
        }
        if (worldEdits.TryGetRequest(request, out var duplicate)) {
            if (duplicate.Actor != session.Account || !mutations.AsSpan().SequenceEqual(duplicate.Mutations)) { throw new InvalidOperationException("Идентификатор запроса занят другой операцией"); }
            foreach (var mutation in duplicate.Mutations) {
                if (deviceLifetimes.TryGetValue(mutation.Key.Incarnation, out var lease) && lease.Current) { SendTransformSnapshot(session, lease, true); }
            }
            SendEditHistory(session); return;
        }
        ValidateTranslation(mutations);
        session.Translation = new(request, mutations, Now + 10);
    }
    private void QueueTranslationReverse(Session session, WorldEditJournal.Entry entry, ulong revision, bool redo) {
        RequireWorldReady(session);
        if (session.Translation != null) { throw new InvalidOperationException("Дождитесь предыдущего перемещения"); }
        if (entry.Actor != session.Account) { throw new UnauthorizedAccessException("Операция принадлежит другому GM"); }
        if (entry.Revision != revision || entry.State != (redo ? WorldEditState.Undone : WorldEditState.Applied)) { throw new InvalidOperationException("История изменилась. Обновите выбранную операцию"); }
        var mutations = new WorldMutation[entry.Mutations.Length];
        for (int i = 0; i < mutations.Length; i++) {
            var original = entry.Mutations[i];
            mutations[i] = new(original.Key, revision, redo ? original.Before : original.After, redo ? original.After : original.Before);
        }
        ValidateTranslation(mutations);
        session.Translation = new(entry.Request, mutations, Now + 10, revision, redo);
    }
    private void ValidateTranslation(WorldMutation[] mutations) {
        Submarine frame = null;
        foreach (var mutation in mutations) {
            if (!deviceLifetimes.TryGetValue(mutation.Key.Incarnation, out var lease) || !lease.Current) { throw new InvalidOperationException("Объект перемещения удалён или заменён"); }
            if (frame == null) { frame = lease.Item.Submarine; }
            if (frame == null || lease.Item.Submarine != frame) { throw new InvalidOperationException("Групповое перемещение требует одну подлодку"); }
            if (worldEdits.Revision(mutation.Key) != mutation.ExpectedRevision || WorldTarget.Read(mutation.Key) != mutation.Before) { throw new InvalidOperationException("Выделение изменено другим GM или симуляцией. Обновите позиции"); }
            WorldTarget.Validate(mutation.Key, mutation.After);
        }
    }
    private bool BindTranslation(WorldMutation[] mutations) {
        bool ready = true;
        foreach (Session peer in sessions.Values) {
            if (!peer.Client.InGame && peer.Bootstrap?.Released != true) { continue; }
            if (peer.TranslationOutbox.Count >= TranslationOutboxCapacity) { ready = false; continue; }
            foreach (var mutation in mutations) {
                if (!deviceLifetimes.TryGetValue(mutation.Key.Incarnation, out var lease) || !lease.Current) { throw new InvalidOperationException("Цель удалена во время подготовки перемещения"); }
                if (!CanProjectWorld(peer, lease)) { ready = false; continue; }
                if (!IsWorldBound(peer, lease)) { ready = false; OfferDevice(peer, lease); }
            }
        }
        return ready;
    }
    private void PumpTranslations(double now) {
        // One bounded intent per GM. Waiting for a peer does not hold a world lock.
        translationSessions.Clear(); translationSessions.AddRange(sessions.Values);
        if (translationSessions.Count == 0) { return; }
        int first = translationCursor % translationSessions.Count, committedCount = 0;
        for (int scanned = 0; scanned < translationSessions.Count; scanned++) {
            int index = (first + scanned) % translationSessions.Count;
            Session session = translationSessions[index];
            translationCursor = (index + 1) % translationSessions.Count;
            var pending = session.Translation; if (pending == null) { continue; }
            try {
                Require(session.Client, InteractionAction.ManageEntities); RequireWorldReady(session); RequireEditingClients();
                if (now >= pending.Deadline) { throw new InvalidOperationException("Клиенты не подтвердили объекты за 10 секунд; перемещение не выполнено"); }
                if (!BindTranslation(pending.Mutations)) { continue; }
                ValidateTranslation(pending.Mutations);
                var entry = pending.ReverseRevision == 0
                    ? worldEdits.ApplyBatch(pending.Request, session.Account, pending.Mutations, WorldTarget, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    : worldEdits.Reverse(pending.Request, session.Account, pending.ReverseRevision, pending.Redo, WorldTarget, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                session.Translation = null;
                PublishTranslation(entry);
                SendEditHistory(session);
                ModLog.Message("GM translation " + entry.Request + " " + session.Account + " count=" + entry.Mutations.Length + " rev=" + entry.Revision);
                if (++committedCount == 2) { break; }
            } catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or AggregateException) {
                session.Translation = null;
                if (error is AggregateException) { ModLog.Error("World transaction restoration failed: " + error); }
                foreach (var mutation in pending.Mutations) {
                    if (deviceLifetimes.TryGetValue(mutation.Key.Incarnation, out var lease) && lease.Current) { SendTransformSnapshot(session, lease, false); }
                }
                SendError(session.Client, error.Message);
            }
        }
    }
    private void PublishTranslation(WorldEditJournal.Entry entry) {
        var rows = new TranslationWire.StateRow[entry.Mutations.Length];
        for (int i = 0; i < rows.Length; i++) {
            var lease = deviceLifetimes[entry.Mutations[i].Key.Incarnation];
            lease.Edited.Add((ushort.MaxValue, LiveItemTransform.PositionKey));
            rows[i] = new(lease.Id, lease.Item.Submarine.ID, lease.PositionState);
        }
        var committed = new ConfirmedTranslation(entry.Request, entry.Revision, rows);
        foreach (Session peer in sessions.Values) {
            if (peer.WorldReady || peer.Bootstrap?.Released == true) { peer.TranslationOutbox.Enqueue(committed); }
        }
    }
    private bool PumpTranslationOutbox(Session session) {
        for (int sent = 0; sent < 2 && session.TranslationOutbox.TryPeek(out var committed); sent++) {
            bool accepted = ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => {
                InteractionProtocol.Header(output, InteractionPacket.WorldTransformBatch); TranslationWire.WriteState(output, committed.Request, committed.Revision, committed.Rows);
            });
            if (!accepted) { return false; }
            session.TranslationOutbox.Dequeue();
        }
        return session.TranslationOutbox.Count == 0;
    }
    private void PumpTransformSnapshots(Session session) {
        sentTransformSnapshots.Clear();
        foreach (var pending in session.PendingTransformSnapshots) {
            if (sentTransformSnapshots.Count == 8) { break; }
            if (!deviceLifetimes.TryGetValue(pending.Key, out var lease) || !lease.Current || !session.DeviceBindings.Contains(lease.Id)) { sentTransformSnapshots.Add(pending.Key); continue; }
            if (!ModNet.Send(InteractionProtocol.Channel, session.Client.Connection, output => WriteTransform(output, lease, pending.Value))) { break; }
            sentTransformSnapshots.Add(pending.Key);
        }
        foreach (Guid token in sentTransformSnapshots) { session.PendingTransformSnapshots.Remove(token); }
    }
    private void ReportEditFailure(IReadMessage input, Session session) {
        Guid request = Guid.ParseExact(input.ReadString(), "N"); string reason = input.ReadString();
        if (request == Guid.Empty || reason.Length > 512 || input.BitPosition > input.LengthBits) { throw new ArgumentException("Некорректная диагностика редактора"); }
        ModLog.Error("World projection failed for " + session.Account + " operation=" + request + ": " + reason);
        GameMain.Server.DisconnectClient(session.Client, PeerDisconnectPacket.Custom("Не удалось применить изменение мира " + request + ". Переподключитесь для восстановления состояния. " + reason));
    }
}
