using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics;
using FarseerPhysics.Dynamics;

namespace IEngine.Execution;

static class BodyIndex
{
    const int AuditInterval = 120;

    static readonly List<PhysicsBody> candidates = new(1024);
    static readonly Dictionary<PhysicsBody, int> slots = new(1024, ReferenceEqualityComparer.Instance);
    static readonly Dictionary<Body, PhysicsBody> owners = new(4096, ReferenceEqualityComparer.Instance);
    static readonly HashSet<PhysicsBody> changed = new(ReferenceEqualityComparer.Instance);
    static int untilAudit;
    public static bool On;
    public static long Missed, Audits, Served, Deltas, Pruned;

    public static int Count => candidates.Count;

    static bool Matches(PhysicsBody b) => !b.Removed && (b.Enabled || b.UserData is Character) && b.FarseerBody.BodyType != BodyType.Static;

    public static void Touch(PhysicsBody b)
    {
        if (!On || b == null) { return; }
        if (b.FarseerBody != null) { owners[b.FarseerBody] = b; }
        if (Matches(b))
        {
            if (!slots.ContainsKey(b)) { Add(b); }
            changed.Remove(b);
        }
        else if (slots.ContainsKey(b))
        {
            // Do not mutate the candidate array in the middle of a GameScreen loop.
            // Process only changed bodies at the next ForUpdate boundary.
            changed.Add(b);
        }
    }

    public static void TouchFarseer(Body body)
    {
        if (On && body != null && owners.TryGetValue(body, out PhysicsBody b)) { Touch(b); }
    }

    public static void Forget(PhysicsBody b)
    {
        if (b == null) { return; }
        if (b.FarseerBody != null) { owners.Remove(b.FarseerBody); }
        changed.Remove(b);
        if (slots.TryGetValue(b, out int i)) { RemoveAt(i); }
    }

    static void Add(PhysicsBody b)
    {
        slots[b] = candidates.Count;
        candidates.Add(b);
    }

    static void RemoveAt(int i)
    {
        int last = candidates.Count - 1;
        PhysicsBody gone = candidates[i];
        if (i != last)
        {
            PhysicsBody moved = candidates[last];
            candidates[i] = moved;
            slots[moved] = i;
        }
        candidates.RemoveAt(last);
        slots.Remove(gone);
    }

    public static void Start()
    {
        On = true;
        Rebuild();
    }

    public static void Stop()
    {
        On = false;
        Clear();
    }

    public static void Clear()
    {
        candidates.Clear();
        slots.Clear();
        owners.Clear();
        changed.Clear();
    }

    static void Rebuild()
    {
        Clear();
        foreach (PhysicsBody b in PhysicsBody.List)
        {
            if (b.FarseerBody != null) { owners[b.FarseerBody] = b; }
            if (Matches(b)) { Add(b); }
        }
        untilAudit = AuditInterval;
    }

    public static List<PhysicsBody> ForUpdate()
    {
        if (!On) { return PhysicsBody.List; }
        if (--untilAudit <= 0) { Audit(); }
        else { ApplyChanges(); }
        Served++;
        return candidates;
    }

    static void ApplyChanges()
    {
        if (changed.Count == 0) { return; }
        foreach (PhysicsBody body in changed)
        {
            if (Matches(body)) { continue; }
            if (slots.TryGetValue(body, out int slot))
            {
                RemoveAt(slot);
                Pruned++;
            }
        }
        Deltas += changed.Count;
        changed.Clear();
    }

    static void Audit()
    {
        untilAudit = AuditInterval;
        Audits++;
        // Foreign mods can bypass the observed properties: repair that drift at a
        // deliberately lower frequency instead of scanning every body every tick.
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            if (!Matches(candidates[i]))
            {
                RemoveAt(i);
                Pruned++;
            }
        }
        changed.Clear();
        Missed += Uncovered(true);
    }

    public static int Uncovered(bool repair)
    {
        int missed = 0;
        foreach (PhysicsBody b in PhysicsBody.List)
        {
            if (slots.ContainsKey(b) || !Matches(b)) { continue; }
            missed++;
            if (repair) { Touch(b); }
        }
        return missed;
    }
}
