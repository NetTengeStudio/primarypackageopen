using System.Diagnostics;
using PrimaryPackage.Interactions;

internal static partial class Program {
    private sealed class EditWorld : IWorldEditTarget {
        internal readonly Dictionary<WorldEditKey, string> Values = new();
        internal int Writes;
        public string Read(in WorldEditKey key) => Values.TryGetValue(key, out string value) ? value : throw new InvalidOperationException("deleted");
        public void Validate(in WorldEditKey key, string value) { Read(key); if (value == "invalid") { throw new ArgumentException("invariant"); } }
        public void Write(in WorldEditKey key, string value) {
            if (value == "failure") { throw new InvalidOperationException("atomic adapter failure"); }
            Values[key] = value; Writes++;
        }
    }
    private sealed class UnrestorableWorld : IWorldEditTarget {
        internal string Value = "before";
        public string Read(in WorldEditKey key) => Value;
        public void Validate(in WorldEditKey key, string value) { }
        public void Write(in WorldEditKey key, string value) {
            if (value == "before") { throw new InvalidOperationException("restore refused"); }
            Value = value; throw new InvalidOperationException("callback failed after mutation");
        }
    }
    private static void WorldEditing() {
        var world = new EditWorld(); var journal = new WorldEditJournal();
        Guid entity = Guid.NewGuid(); WorldEditKey a = new(entity, 0, "A|Power"), b = new WorldEditKey(entity, 1, "A|Power");
        world.Values[a] = world.Values[b] = "0";
        Guid request = Guid.NewGuid(); var first = journal.Apply(request, "gm1", a, 0, "0", "1", world, 1);
        journal.Apply(Guid.NewGuid(), "gm2", b, 0, "0", "2", world, 2);
        Check(world.Values[a] == "1" && world.Values[b] == "2", "two component instances and independent GMs preserve both edits");
        Check(ReferenceEquals(first, journal.Apply(request, "gm1", a, 0, "0", "1", world, 3)) && world.Writes == 2, "retransmission is idempotent");
        Reject<InvalidOperationException>(() => journal.Apply(request, "gm2", a, 0, "0", "1", world, 3), "another actor cannot reuse an operation ID");
        Reject<InvalidOperationException>(() => journal.Apply(Guid.NewGuid(), "gm2", a, 0, "0", "3", world, 3), "stale revision is rejected before mutation");
        Reject<UnauthorizedAccessException>(() => journal.Reverse(request, "gm2", first.Revision, false, world, 4), "GM cannot reverse another author's operation");
        journal.Reverse(request, "gm1", first.Revision, false, world, 4);
        Check(world.Values[a] == "0" && world.Values[b] == "2", "undo does not erase another GM's independent component change");
        ulong undone = first.Revision;
        journal.Apply(request, "gm1", a, 0, "0", "1", world, 4);
        Check(first.Revision == undone && world.Values[a] == "0", "duplicate original request cannot replay an undone operation");
        journal.Reverse(request, "gm1", first.Revision, true, world, 5);
        Check(world.Values[a] == "1" && first.Revision > undone, "redo has a new revision");
        Check(journal.Changes.Count == 4 && journal.Changes[0].Kind == WorldEditChangeKind.Apply && journal.Changes[2].Kind == WorldEditChangeKind.Undo && journal.Changes[3].Kind == WorldEditChangeKind.Redo && journal.Changes[0].Timestamp == 1, "immutable audit retains original operation and both inverse events");
        ulong beforeFailure = journal.Revision(a); int count = journal.Entries.Count;
        Reject<ArgumentException>(() => journal.Apply(Guid.NewGuid(), "gm1", a, beforeFailure, "1", "invalid", world, 6), "invariant failure rejects before writing");
        Reject<InvalidOperationException>(() => journal.Apply(Guid.NewGuid(), "gm1", a, beforeFailure, "1", "failure", world, 6), "adapter failure does not publish a revision");
        Check(journal.Revision(a) == beforeFailure && journal.Entries.Count == count && world.Values[a] == "1", "rejected operation leaves world and journal unchanged");
        world.Values[a] = "simulation";
        Reject<InvalidOperationException>(() => journal.Reverse(request, "gm1", first.Revision, false, world, 7), "undo cannot overwrite a subsequent simulation change");
        world.Values[a] = "1";
        journal.Apply(Guid.NewGuid(), "gm2", a, journal.Revision(a), "1", "2", world, 8);
        journal.Apply(Guid.NewGuid(), "gm2", a, journal.Revision(a), "2", "1", world, 9);
        Reject<InvalidOperationException>(() => journal.Reverse(request, "gm1", first.Revision, false, world, 10), "ABA values cannot hide later GM revisions from undo");
        world.Values.Remove(a); var replacement = new WorldEditKey(Guid.NewGuid(), a.Component, a.Property); world.Values[replacement] = "0";
        Reject<InvalidOperationException>(() => journal.Apply(Guid.NewGuid(), "gm1", a, journal.Revision(a), "1", "7", world, 11), "deleted incarnation cannot target a replacement with the same native address");
        journal.Apply(Guid.NewGuid(), "gm2", replacement, 0, "0", "new", world, 12);
        Check(world.Values[replacement] == "new", "replacement has an independent revision namespace");
        journal.Clear();
        world.Values[a] = "0"; world.Values[b] = "0";
        var group = new[] { new WorldMutation(a, 0, "0", "group-a"), new WorldMutation(b, 0, "0", "group-b") };
        Guid groupRequest = Guid.NewGuid();
        var grouped = journal.ApplyBatch(groupRequest, "gm", group, world, 15);
        Check(journal.Revision(a) == journal.Revision(b) && world.Values[a] == "group-a" && world.Values[b] == "group-b", "group changes receive one committed revision");
        group[0] = new(a, 0, "0", "caller-mutated");
        Check(grouped.Mutations[0].After == "group-a", "journal owns a copy of the submitted mutation array");
        journal.Reverse(groupRequest, "gm", grouped.Revision, false, world, 16);
        Check(world.Values[a] == "0" && world.Values[b] == "0", "group undo restores every member");
        journal.Reverse(groupRequest, "gm", grouped.Revision, true, world, 17);
        journal.Apply(Guid.NewGuid(), "other", b, journal.Revision(b), "group-b", "later", world, 18);
        Reject<InvalidOperationException>(() => journal.Reverse(groupRequest, "gm", grouped.Revision, false, world, 19), "one conflicting member blocks the complete group inverse");
        Check(world.Values[a] == "group-a" && world.Values[b] == "later", "conflicting group inverse leaves all members unchanged");
        ulong groupRevisionA = journal.Revision(a), groupRevisionB = journal.Revision(b); int beforeBatchFailure = journal.Changes.Count;
        Reject<InvalidOperationException>(() => journal.ApplyBatch(Guid.NewGuid(), "gm", new[] { new WorldMutation(a, groupRevisionA, "group-a", "temporary"), new WorldMutation(b, groupRevisionB, "later", "failure") }, world, 20), "failure of a later member aborts the complete transaction");
        Check(world.Values[a] == "group-a" && world.Values[b] == "later" && journal.Revision(a) == groupRevisionA && journal.Changes.Count == beforeBatchFailure, "failed group restores earlier writes and publishes no journal entries");
        Reject<ArgumentException>(() => journal.ApplyBatch(Guid.NewGuid(), "gm", new[] { new WorldMutation(a, groupRevisionA, "group-a", "x"), new WorldMutation(a, groupRevisionA, "group-a", "y") }, world, 21), "duplicate targets inside a group are rejected before writing");
        journal.Clear(); Check(journal.Entries.Count == 0 && journal.Revision(replacement) == 0, "round clears journal ownership");
        var faultKey = new WorldEditKey(Guid.NewGuid(), 0, "fault"); var faultWorld = new UnrestorableWorld();
        Reject<AggregateException>(() => journal.Apply(Guid.NewGuid(), "gm", faultKey, 0, "before", "after", faultWorld, 22), "restoration failure retains all errors");
        Check(journal.Faulted && journal.Entries.Count == 0, "uncertain world state faults the journal without publishing a successful transaction");
        Reject<InvalidOperationException>(() => journal.Apply(Guid.NewGuid(), "gm", b, 0, "0", "blocked", world, 23), "a failed restoration blocks all further editor writes");
        journal.Clear(); Check(!journal.Faulted, "new round resets the transaction fault");
        var capacityKey = new WorldEditKey(Guid.NewGuid(), 0, "field"); world.Values[capacityKey] = "0";
        for (int i = 0; i < WorldEditJournal.Capacity; i++) {
            string old = world.Values[capacityKey]; journal.Apply(Guid.NewGuid(), "gm", capacityKey, journal.Revision(capacityKey), old, (i + 1).ToString(), world, i);
        }
        Reject<InvalidOperationException>(() => journal.Apply(Guid.NewGuid(), "gm", capacityKey, journal.Revision(capacityKey), world.Values[capacityKey], "overflow", world, 9999), "full journal rejects before world mutation");
        Check(journal.Entries.Count == WorldEditJournal.Capacity && world.Values[capacityKey] != "overflow", "capacity is bounded without silent history eviction");
    }
    private static void PickingIndex() {
        var index = new BoundsIndex<object>(); var results = new List<object>();
        object wall = new(), point = new(), removed = new();
        index.Upsert(wall, -10000, -10, 10000, 10); index.Upsert(point, -256, -256, -256, -256);
        index.Query(9999, 0, 9999, 0, results);
        Check(results.Count == 1 && results[0] == wall, "large wall selectable at its edge far from center");
        results.Clear(); index.Query(-256, -256, -256, -256, results);
        Check(results.Count == 1 && results[0] == point, "negative coordinates and exact grid boundary");
        index.Upsert(removed, 0, 0, 1024, 1024); index.Remove(removed); results.Clear(); index.Query(-1, -1, 1, 1, results);
        Check(!results.Contains(removed), "removed object leaves all broad-phase cells");
        index.Upsert(wall, 900, 900, 901, 901); results.Clear(); index.Query(9999, 0, 9999, 0, results);
        Check(results.Count == 0, "moving a large object removes its previous coverage");
        var random = new Random(90210); var bounds = new Dictionary<object, (float X, float Y, float W, float H)>();
        for (int i = 0; i < 2000; i++) { object entity = new(); var rect = (X: random.Next(-10000, 10000), Y: random.Next(-10000, 10000), W: random.Next(1, 600), H: random.Next(1, 600)); bounds[entity] = rect; index.Upsert(entity, rect.X, rect.Y, rect.X + rect.W, rect.Y + rect.H); }
        for (int q = 0; q < 1000; q++) {
            float x = random.Next(-10000, 10000), y = random.Next(-10000, 10000), size = random.Next(0, 2000);
            results.Clear(); index.Query(x, y, x + size, y + size, results);
            var actual = new HashSet<object>(results);
            Check(actual.Count == results.Count, "marquee contains no duplicates across cells");
            foreach (var pair in bounds) {
                var rect = pair.Value; bool expected = rect.X <= x + size && rect.X + rect.W >= x && rect.Y <= y + size && rect.Y + rect.H >= y;
                if (actual.Contains(pair.Key) != expected) { Check(false, "broad phase differs from brute force"); break; }
            }
        }
        results.Clear(); index.Query(-100000, -100000, 100000, 100000, results);
        Check(results.Count == index.Count, "very wide selection uses complete bounded scan");
        Reject<ArgumentOutOfRangeException>(() => index.Upsert(new(), float.NaN, 0, 1, 1), "index rejects NaN bounds");
        Reject<ArgumentOutOfRangeException>(() => index.Query(1, 0, -1, 1, results), "index rejects inverted bounds");
    }
    private static void HorrorSignals() {
        for (int preset = 0; preset < 3; preset++) { HorrorPresets.Create(preset, "crawler").Validate(); }
        for (int i = 0; i < 10000; i++) {
            double time = i / 1000.0; var sample = HorrorSignal.Sample(time, 10, .75f, 7);
            Check(sample == HorrorSignal.Sample(time, 10, .75f, 7), "signal presentation is deterministic");
            Check(float.IsFinite(sample.Darkness) && sample.Darkness >= 0 && sample.Darkness <= .85f && Math.Abs(sample.Displacement) <= 1, "signal overlay stays within finite limits");
        }
        Check(HorrorSignal.Sample(-1, 10, 1, 0) == default && HorrorSignal.Sample(10, 10, 1, 0) == default, "signal has no output outside its lifetime");
        Reject<ArgumentOutOfRangeException>(() => HorrorSignal.Sample(double.NaN, 1, 1, 0), "nonfinite visual clock is rejected");
    }
    private static object MeasureEditor() {
        long constructionStart = Stopwatch.GetTimestamp(), constructionBytes = GC.GetAllocatedBytesForCurrentThread();
        var index = new BoundsIndex<object>(); var results = new List<object>(1024);
        for (int y = 0; y < 100; y++) { for (int x = 0; x < 200; x++) { index.Upsert(new object(), x * 100, y * 100, x * 100 + 80, y * 100 + 80); } }
        constructionBytes = GC.GetAllocatedBytesForCurrentThread() - constructionBytes; double constructionMilliseconds = (Stopwatch.GetTimestamp() - constructionStart) * 1000.0 / Stopwatch.Frequency;
        for (int i = 0; i < 1000; i++) { results.Clear(); index.Query(1000, 1000, 1100, 1100, results); }
        long bytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp(); const int samples = 100000;
        for (int i = 0; i < samples; i++) { results.Clear(); index.Query(1000, 1000, 1100, 1100, results); }
        long elapsed = Stopwatch.GetTimestamp() - start; bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Check(bytes == 0, "warmed broad phase allocates zero bytes for repeated local selection");
        object moving = new();
        for (int i = 0; i < 1000; i++) { float x = (i & 1) * 1000; index.Upsert(moving, x, 20000, x + 80, 20080); }
        long movingBytes = GC.GetAllocatedBytesForCurrentThread(), movingStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++) { float x = (i & 1) * 1000; index.Upsert(moving, x, 20000, x + 80, 20080); }
        movingBytes = GC.GetAllocatedBytesForCurrentThread() - movingBytes;
        double movingMeanMicroseconds = (Stopwatch.GetTimestamp() - movingStart) * 1e6 / Stopwatch.Frequency / samples;
        Check(movingBytes == 0, "warmed movement between empty cells reuses bounded bucket buffers");
        return new { scenario = "AABB broad phase, 20000 static entities, local marquee", constructionBytes, constructionMilliseconds, movingBytes, movingMeanMicroseconds, samples, meanMicroseconds = elapsed * 1e6 / Stopwatch.Frequency / samples, allocatedBytes = bytes,
            scope = "production pure index, one thread, warmed, synthetic rectangles; excludes engine hooks, GPU, network and world construction" };
    }
}
