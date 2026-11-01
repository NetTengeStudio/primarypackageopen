using System;
using System.Collections.Generic;

namespace PrimaryPackage.Interactions;

// Native IDs locate objects. Incarnations identify their lifetime in this round.
internal readonly record struct WorldEditKey(Guid Incarnation, ushort Component, string Property);
internal enum WorldEditState : byte { Applied, Undone }
internal enum WorldEditChangeKind : byte { Apply, Undo, Redo }

internal interface IWorldEditTarget {
    string Read(in WorldEditKey key);
    void Validate(in WorldEditKey key, string value);
    // Implementations must either complete or leave their previous value intact.
    void Write(in WorldEditKey key, string value);
}

/// <summary>Server-thread ordering, CAS and conditional inverses. Values stay owned by the world.
/// Undo restores a setting, never elapsed simulation or physical consequences.</summary>
internal sealed class WorldEditJournal {
    internal const int Capacity = 4096;
    internal sealed class Entry {
        internal Guid Request;
        internal string Actor, Before, After;
        internal WorldEditKey Key;
        internal ulong Revision, PreviousRevision;
        internal long Timestamp;
        internal WorldEditState State;
    }
    private readonly Dictionary<WorldEditKey, ulong> revisions = new();
    private readonly Dictionary<Guid, Entry> requests = new();
    private readonly List<Entry> entries = new();
    internal readonly record struct Change(ulong Sequence, Guid Request, string Actor, WorldEditKey Key, string Before, string After, long Timestamp, WorldEditChangeKind Kind);
    private readonly List<Change> changes = new();
    private ulong sequence;
    internal IReadOnlyList<Entry> Entries => entries;
    internal IReadOnlyList<Change> Changes => changes;
    internal ulong Revision(in WorldEditKey key) => revisions.TryGetValue(key, out ulong value) ? value : 0;
    internal Entry Apply(Guid request, string actor, in WorldEditKey key, ulong expectedRevision,
        string expectedValue, string value, IWorldEditTarget world, long timestamp) {
        if (request == Guid.Empty || key.Incarnation == Guid.Empty || string.IsNullOrEmpty(actor) ||
            string.IsNullOrEmpty(key.Property) || key.Property.Length > 288 || value == null || value.Length > 512 ||
            expectedValue == null || expectedValue.Length > 512) { throw new ArgumentException("Недопустимая операция мира"); }
        if (requests.TryGetValue(request, out Entry duplicate)) {
            if (duplicate.Actor != actor || duplicate.Key != key || duplicate.Before != expectedValue ||
                duplicate.After != value || duplicate.PreviousRevision != expectedRevision) {
                throw new InvalidOperationException("Идентификатор запроса уже занят другой операцией");
            }
            return duplicate;
        }
        if (changes.Count >= Capacity || sequence == ulong.MaxValue) { throw new InvalidOperationException("История мира заполнена; новая правка не выполнена"); }
        RequireCurrent(key, expectedRevision, expectedValue, world);
        world.Validate(key, value);
        var entry = new Entry { Request = request, Actor = actor, Key = key, Before = expectedValue, After = value,
            PreviousRevision = expectedRevision, Timestamp = timestamp };
        world.Write(key, value);
        entry.Revision = ++sequence;
        revisions[key] = entry.Revision;
        requests.Add(request, entry); entries.Add(entry);
        changes.Add(new(entry.Revision, request, actor, key, expectedValue, value, timestamp, WorldEditChangeKind.Apply));
        return entry;
    }
    internal Entry Reverse(Guid request, string actor, ulong expectedRevision, bool redo, IWorldEditTarget world, long timestamp) {
        if (!requests.TryGetValue(request, out Entry entry) || entry.Actor != actor) { throw new UnauthorizedAccessException("Операция не принадлежит этому GM"); }
        if (entry.Revision != expectedRevision || entry.State != (redo ? WorldEditState.Undone : WorldEditState.Applied)) {
            throw new InvalidOperationException("История уже изменилась. Обновите выбранную операцию");
        }
        if (changes.Count >= Capacity || sequence == ulong.MaxValue) { throw new InvalidOperationException("История мира заполнена; обратная операция не выполнена"); }
        string before = redo ? entry.Before : entry.After, after = redo ? entry.After : entry.Before;
        RequireCurrent(entry.Key, entry.Revision, before, world);
        world.Validate(entry.Key, after);
        world.Write(entry.Key, after);
        entry.State = redo ? WorldEditState.Applied : WorldEditState.Undone;
        entry.Revision = ++sequence; entry.Timestamp = timestamp;
        revisions[entry.Key] = entry.Revision;
        changes.Add(new(entry.Revision, request, actor, entry.Key, before, after, timestamp, redo ? WorldEditChangeKind.Redo : WorldEditChangeKind.Undo));
        return entry;
    }
    private void RequireCurrent(in WorldEditKey key, ulong revision, string value, IWorldEditTarget world) {
        if (Revision(key) != revision || world.Read(key) != value) {
            throw new InvalidOperationException("Объект изменён другим GM или симуляцией. Обновите состояние");
        }
    }
    internal void Clear() { revisions.Clear(); requests.Clear(); entries.Clear(); changes.Clear(); sequence = 0; }
}
