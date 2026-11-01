using System;
using System.Collections.Generic;

namespace PrimaryPackage.Interactions;

internal sealed class TimedLeases<T> where T : class {
    internal readonly struct Lease {
        internal readonly Guid Id;
        internal readonly T Target;
        internal readonly double End;
        internal Lease(Guid id, T target, double end) { Id = id; Target = target; End = end; }
    }
    private readonly int capacity;
    private readonly Dictionary<Guid, Lease> leases = new();
    private readonly Dictionary<T, double> ends = new(ReferenceEqualityComparer.Instance);
    private readonly List<Guid> expired = new();
    internal TimedLeases(int capacity) { if (capacity < 1) { throw new ArgumentOutOfRangeException(nameof(capacity)); } this.capacity = capacity; }
    internal Dictionary<Guid, Lease>.ValueCollection Entries => leases.Values;
    internal bool TryGetEnd(T target, out double end) => ends.TryGetValue(target, out end);
    internal void Start(Guid id, T target, double end) {
        if (id == Guid.Empty || target == null || !double.IsFinite(end)) { throw new ArgumentException("Invalid temporary effect lease"); }
        bool exists = leases.TryGetValue(id, out var previous);
        if (exists && previous.Target != target) { throw new ArgumentException("A lease cannot change its target"); }
        if (!exists && leases.Count >= capacity) { throw new InvalidOperationException("Temporary effect capacity reached"); }
        leases[id] = new Lease(id, target, end); Rebuild(target);
    }
    internal void Cancel(Guid id) { if (leases.Remove(id, out var removed)) { Rebuild(removed.Target); } }
    internal void Expire(double now, Predicate<T> invalid = null) {
        if (!double.IsFinite(now)) { throw new ArgumentOutOfRangeException(nameof(now)); }
        expired.Clear();
        foreach (var lease in leases.Values) { if (lease.End <= now || invalid?.Invoke(lease.Target) == true) { expired.Add(lease.Id); } }
        foreach (Guid id in expired) { Cancel(id); }
        expired.Clear();
    }
    private void Rebuild(T target) {
        double end = double.NegativeInfinity;
        foreach (var lease in leases.Values) { if (ReferenceEquals(lease.Target, target)) { end = Math.Max(end, lease.End); } }
        if (double.IsNegativeInfinity(end)) { ends.Remove(target); } else { ends[target] = end; }
    }
    internal void Clear() { leases.Clear(); ends.Clear(); expired.Clear(); }
}
