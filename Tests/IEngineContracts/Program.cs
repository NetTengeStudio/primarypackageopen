using IEngine.Core;

internal static class Program
{
    // Deliberately equal by value: membership must still use reference identity.
    sealed class Entry
    {
        public bool Tagged;
        public override bool Equals(object obj) => obj is Entry;
        public override int GetHashCode() => 0;
    }

    static int checks;
    static readonly Predicate<Entry> Included = static entry => entry is { Tagged: true };

    static int Main()
    {
        try
        {
            Mutations();
            FailuresAndReset();
            Allocations();
            Console.WriteLine($"IEngineContracts: {checks} checks, 0 failures");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void Check(bool value, string name)
    {
        checks++;
        if (!value) { throw new InvalidOperationException(name); }
    }

    static void Mutations()
    {
        var snapshot = new ReferenceSnapshot<Entry>();
        var random = new Random(620031);
        Entry[] entries = Enumerable.Range(0, 128).Select(i => new Entry { Tagged = i % 4 == 0 }).ToArray();
        var source = new List<Entry>();
        var changes = new List<Entry>();
        Entry[] previous = Array.Empty<Entry>();
        Action<Entry> changed = changes.Add;
        for (int round = 0; round < 5000; round++)
        {
            switch (random.Next(7))
            {
                case 0: source.Insert(random.Next(source.Count + 1), entries[random.Next(entries.Length)]); break;
                case 1 when source.Count > 0: source.RemoveAt(random.Next(source.Count)); break;
                case 2 when source.Count > 0: source[random.Next(source.Count)] = entries[random.Next(entries.Length)]; break;
                case 3: source.Reverse(); break;
                case 4: entries[random.Next(entries.Length)].Tagged ^= true; break;
                case 5 when round % 19 == 0: source.Clear(); break;
                case 6: source.Add(null); break;
            }
            Entry[] current = source.Where(entry => Included(entry)).ToArray();
            var oldSet = new HashSet<Entry>(previous, ReferenceEqualityComparer.Instance);
            var newSet = new HashSet<Entry>(current, ReferenceEqualityComparer.Instance);
            Entry[] expected = previous.Where(entry => !newSet.Contains(entry))
                .Concat(current.Where(entry => !oldSet.Contains(entry))).ToArray();
            changes.Clear();
            snapshot.Update(source, Included, changed);
            Check(changes.SequenceEqual(expected, ReferenceEqualityComparer.Instance), $"membership and notification order at mutation {round}");
            previous = current;
        }
    }

    static void FailuresAndReset()
    {
        var a = new Entry { Tagged = true };
        var b = new Entry { Tagged = true };
        var source = new List<Entry> { a };
        var snapshot = new ReferenceSnapshot<Entry>();
        var changes = new List<Entry>();
        snapshot.Update(source, Included, null);
        source[0] = b;
        bool threw = false;
        try { snapshot.Update(source, Included, static _ => throw new ApplicationException()); }
        catch (ApplicationException) { threw = true; }
        Check(threw, "callback failure propagates");
        snapshot.Update(source, Included, changes.Add);
        Check(changes.Count == 2 && ReferenceEquals(changes[0], a) && ReferenceEquals(changes[1], b), "failed callback does not commit snapshot");
        changes.Clear();
        threw = false;
        try { snapshot.Update(source, static _ => throw new ApplicationException(), changes.Add); }
        catch (ApplicationException) { threw = true; }
        Check(threw, "selection failure propagates");
        snapshot.Update(source, Included, changes.Add);
        Check(changes.Count == 0, "selection failure preserves committed snapshot");
        snapshot.Clear();
        snapshot.Update(source, Included, changes.Add);
        Check(changes.Count == 1 && ReferenceEquals(changes[0], b), "reset releases previous membership");
        changes.Clear();
        source.Clear();
        snapshot.Update(source, Included, changes.Add);
        Check(changes.Count == 1 && ReferenceEquals(changes[0], b), "clear source notifies removal");
        source.Add(a);
        snapshot.Update(source, Included, _ => source.Clear());
        Check(source.Count == 0, "callbacks execute after source enumeration");
        snapshot.Update(source, Included, null);
        snapshot.Update(source, Included, changes.Add);
        Check(changes.Count == 1, "silent update advances membership");
    }

    static void Allocations()
    {
        var a = new Entry { Tagged = true };
        var b = new Entry { Tagged = true };
        var source = Enumerable.Range(0, 4096).Select(_ => new Entry()).ToList();
        source[0] = a;
        var snapshot = new ReferenceSnapshot<Entry>();
        int notifications = 0;
        Action<Entry> changed = _ => notifications++;
        for (int i = 0; i < 128; i++)
        {
            source[0] = (i & 1) == 0 ? a : b;
            snapshot.Update(source, Included, changed);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
        {
            source[0] = (i & 1) == 0 ? a : b;
            snapshot.Update(source, Included, changed);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, $"warmed snapshot allocation: {allocated} bytes");
        Check(notifications > 4000, "allocation check exercised changing tagged membership");
        Console.WriteLine($"Snapshot: 4096 entries, 2000 warmed membership changes, {allocated} B allocated");
    }
}
