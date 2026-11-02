using System;
using System.Collections.Generic;

namespace PrimaryPackage.Interactions;

internal readonly record struct WorldEditKey(Guid Incarnation, ushort Component, string Property);
internal readonly record struct WorldMutation(WorldEditKey Key, ulong ExpectedRevision, string Before, string After);
internal enum WorldEditState : byte { Applied, Undone }
internal enum WorldEditChangeKind : byte { Apply, Undo, Redo }

internal interface IWorldEditTarget {
    string Read(in WorldEditKey key);
    void Validate(in WorldEditKey key, string value);
    void Write(in WorldEditKey key, string value);
}

/// <summary>Server-thread transaction ordering, CAS, conditional inverses and immutable audit.
/// World state remains owned by adapters. An inverse does not rewind elapsed simulation.</summary>
internal sealed class WorldEditJournal {
    internal const int Capacity = 4096, MaxBatch = 64;
    internal const int MaxPropertyAddressLength = 160 + 1 + 128;
    internal sealed class Entry {
        internal readonly Guid Request;
        internal readonly string Actor;
        internal readonly WorldMutation[] Mutations;
        internal ulong Revision;
        internal long Timestamp;
        internal WorldEditState State;
        internal WorldEditKey Key => Mutations[0].Key;
        internal string Before => Mutations[0].Before;
        internal string After => Mutations[0].After;
        internal ulong PreviousRevision => Mutations[0].ExpectedRevision;
        internal Entry(Guid request, string actor, WorldMutation[] mutations, long timestamp) { Request = request; Actor = actor; Mutations = mutations; Timestamp = timestamp; }
    }
    internal readonly record struct Change(ulong Sequence, Guid Request, string Actor, WorldEditKey Key, string Before, string After, long Timestamp, WorldEditChangeKind Kind);
    private readonly Dictionary<WorldEditKey, ulong> revisions = new();
    private readonly Dictionary<Guid, Entry> requests = new();
    private readonly List<Entry> entries = new();
    private readonly List<Change> changes = new();
    private ulong sequence;
    private bool applying;
    internal bool Faulted { get; private set; }
    internal IReadOnlyList<Entry> Entries => entries;
    internal IReadOnlyList<Change> Changes => changes;
    internal ulong Head => sequence;
    internal bool TryGetRequest(Guid request, out Entry entry) => requests.TryGetValue(request, out entry);
    internal ulong Revision(in WorldEditKey key) => revisions.TryGetValue(key, out ulong value) ? value : 0;
    internal Entry Apply(Guid request, string actor, in WorldEditKey key, ulong expectedRevision, string expectedValue, string value, IWorldEditTarget world, long timestamp) =>
        ApplyBatch(request, actor, new[] { new WorldMutation(key, expectedRevision, expectedValue, value) }, world, timestamp);
    internal Entry ApplyBatch(Guid request, string actor, ReadOnlySpan<WorldMutation> mutations, IWorldEditTarget world, long timestamp) {
        if (applying) { throw new InvalidOperationException("Редакторская транзакция не допускает повторного входа из игрового callback"); }
        if (request == Guid.Empty || string.IsNullOrEmpty(actor) || actor.Length > 128 || mutations.Length is < 1 or > MaxBatch) { throw new ArgumentException("Недопустимая операция мира"); }
        if (requests.TryGetValue(request, out Entry duplicate)) {
            if (duplicate.Actor != actor || !mutations.SequenceEqual(duplicate.Mutations)) { throw new InvalidOperationException("Идентификатор запроса уже занят другой операцией"); }
            return duplicate;
        }
        EnsureCapacity(mutations.Length);
        var keys = new HashSet<WorldEditKey>(mutations.Length);
        foreach (var mutation in mutations) {
            if (mutation.Key.Incarnation == Guid.Empty || string.IsNullOrEmpty(mutation.Key.Property) || mutation.Key.Property.Length > MaxPropertyAddressLength ||
                mutation.Before == null || mutation.After == null || mutation.Before.Length > 512 || mutation.After.Length > 512 || !keys.Add(mutation.Key)) { throw new ArgumentException("Недопустимый или повторный адрес изменения мира"); }
        }
        // Own caller data and reserve publication storage before the first engine mutation.
        var entry = new Entry(request, actor, mutations.ToArray(), timestamp);
        requests.EnsureCapacity(requests.Count + 1); entries.EnsureCapacity(entries.Count + 1);
        revisions.EnsureCapacity(revisions.Count + mutations.Length);
        applying = true;
        try {
            foreach (var mutation in entry.Mutations) { RequireCurrent(mutation.Key, mutation.ExpectedRevision, mutation.Before, world); world.Validate(mutation.Key, mutation.After); }
            ExecuteOwned(entry.Mutations, true, world);
            entry.Revision = ++sequence;
            Publish(entry, WorldEditChangeKind.Apply, timestamp);
            requests.Add(request, entry); entries.Add(entry);
            return entry;
        } finally { applying = false; }
    }
    internal Entry Reverse(Guid request, string actor, ulong expectedRevision, bool redo, IWorldEditTarget world, long timestamp) {
        if (applying) { throw new InvalidOperationException("Редакторская транзакция уже выполняется"); }
        if (!requests.TryGetValue(request, out Entry entry) || entry.Actor != actor) { throw new UnauthorizedAccessException("Операция не принадлежит этому GM"); }
        if (entry.Revision != expectedRevision || entry.State != (redo ? WorldEditState.Undone : WorldEditState.Applied)) { throw new InvalidOperationException("История уже изменилась. Обновите выбранную операцию"); }
        EnsureCapacity(entry.Mutations.Length);
        applying = true;
        try {
            foreach (var mutation in entry.Mutations) {
                RequireCurrent(mutation.Key, entry.Revision, redo ? mutation.Before : mutation.After, world);
                world.Validate(mutation.Key, redo ? mutation.After : mutation.Before);
            }
            ExecuteOwned(entry.Mutations, redo, world);
            entry.State = redo ? WorldEditState.Applied : WorldEditState.Undone;
            entry.Revision = ++sequence; entry.Timestamp = timestamp;
            Publish(entry, redo ? WorldEditChangeKind.Redo : WorldEditChangeKind.Undo, timestamp);
            return entry;
        } finally { applying = false; }
    }
    private void EnsureCapacity(int count) {
        if (Faulted) { throw new InvalidOperationException("Редактор остановлен до следующего раунда: предыдущую транзакцию не удалось полностью восстановить"); }
        if (changes.Count > Capacity - count || sequence == ulong.MaxValue) { throw new InvalidOperationException("История мира заполнена; операция не выполнена"); }
        changes.EnsureCapacity(changes.Count + count);
    }
    private void ExecuteOwned(WorldMutation[] mutations, bool forward, IWorldEditTarget world) {
        try { Execute(mutations, forward, world); }
        catch (AggregateException) { Faulted = true; throw; }
    }
    private void Publish(Entry entry, WorldEditChangeKind kind, long timestamp) {
        foreach (var mutation in entry.Mutations) {
            revisions[mutation.Key] = entry.Revision;
            changes.Add(new(entry.Revision, entry.Request, entry.Actor, mutation.Key,
                kind == WorldEditChangeKind.Undo ? mutation.After : mutation.Before,
                kind == WorldEditChangeKind.Undo ? mutation.Before : mutation.After, timestamp, kind));
        }
    }
    internal static void Execute(WorldMutation[] mutations, bool forward, IWorldEditTarget world) {
        try {
            foreach (var mutation in mutations) { world.Write(mutation.Key, forward ? mutation.After : mutation.Before); }
            foreach (var mutation in mutations) {
                if (world.Read(mutation.Key) != (forward ? mutation.After : mutation.Before)) { throw new InvalidOperationException("Зависимое игровое состояние отклонило результат транзакции"); }
            }
        } catch (Exception failure) {
            List<Exception> errors = null;
            for (int i = mutations.Length - 1; i >= 0; i--) {
                var mutation = mutations[i];
                try { world.Write(mutation.Key, forward ? mutation.Before : mutation.After); }
                catch (Exception rollback) { (errors ??= new() { failure }).Add(rollback); }
            }
            foreach (var mutation in mutations) {
                try { if (world.Read(mutation.Key) != (forward ? mutation.Before : mutation.After)) { throw new InvalidOperationException("Восстановленное состояние не совпадает с исходным"); } }
                catch (Exception rollback) { (errors ??= new() { failure }).Add(rollback); }
            }
            if (errors != null) { throw new AggregateException("Не удалось полностью восстановить редакторскую транзакцию", errors); }
            throw;
        }
    }
    private void RequireCurrent(in WorldEditKey key, ulong revision, string value, IWorldEditTarget world) {
        if (Revision(key) != revision || world.Read(key) != value) { throw new InvalidOperationException("Объект изменён другим GM или симуляцией. Обновите состояние"); }
    }
    internal void Clear() {
        if (applying) { throw new InvalidOperationException("Нельзя очистить журнал во время транзакции"); }
        revisions.Clear(); requests.Clear(); entries.Clear(); changes.Clear(); sequence = 0; Faulted = false;
    }
}
