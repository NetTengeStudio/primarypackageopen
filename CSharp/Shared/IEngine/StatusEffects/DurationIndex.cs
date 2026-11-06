using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Barotrauma;
using HarmonyLib;

namespace IEngine.StatusEffects;

// Only Parent is indexed: Targets remains mutable, including during UpdateAll.
// The source list owns order and lifetime; its exact version invalidates this view.
sealed class DurationIndex
{
    struct Chain
    {
        public int Head, Tail;
    }

    static readonly AccessTools.FieldRef<List<DurationListElement>, int> Version =
        AccessTools.FieldRefAccess<List<DurationListElement>, int>("_version");

    readonly List<DurationListElement> source;
    readonly Dictionary<StatusEffect, Chain> chains = new(ReferenceEqualityComparer.Instance);
    DurationListElement[] elements = Array.Empty<DurationListElement>();
    int[] next = Array.Empty<int>();
    int count, version;
    bool ready, valid;

    public long Rebuilds { get; private set; }
    public long Appends { get; private set; }
    public long Candidates { get; private set; }

    public DurationIndex(List<DurationListElement> source) => this.source = source;

    public DurationListElement FindSingle(StatusEffect parent, ISerializableEntity target)
    {
        if (!Refresh() || parent == null) { return ReferenceSingle(parent, target); }
        if (!chains.TryGetValue(parent, out Chain chain)) { return null; }
        for (int i = chain.Head; i >= 0; i = next[i])
        {
            DurationListElement element = elements[i];
            Candidates++;
            List<ISerializableEntity> targets = element.Targets;
            if ((targets.Count == 0 ? null : targets[0]) == target) { return element; }
        }
        return null;
    }

    public DurationListElement FindMany(StatusEffect parent, List<ISerializableEntity> targets)
    {
        if (!Refresh() || parent == null) { return ReferenceMany(parent, targets); }
        if (!chains.TryGetValue(parent, out Chain chain)) { return null; }
        for (int i = chain.Head; i >= 0; i = next[i])
        {
            DurationListElement element = elements[i];
            Candidates++;
            // Retain Enumerable.SequenceEqual's equality and current target order.
            if (CollectionsMarshal.AsSpan(element.Targets).SequenceEqual(CollectionsMarshal.AsSpan(targets))) { return element; }
        }
        return null;
    }

    public void Append(DurationListElement element)
    {
        bool current = ready && valid && version == Version(source);
        source.Add(element);
        if (!current) { ready = false; return; }
        EnsureCapacity(source.Count);
        if (element == null) { ready = false; return; }
        Add(element);
        version = Version(source);
        Appends++;
    }

    bool Refresh()
    {
        int current = Version(source);
        if (ready && version == current) { return valid; }
        // Insert, replacement, reorder, removal and unobserved Add all rebuild.
        // Count/version deltas alone cannot establish that a mutation was an append.
        Reset();
        EnsureCapacity(source.Count);
        valid = true;
        foreach (DurationListElement element in source)
        {
            if (element == null) { valid = false; break; }
            Add(element);
        }
        if (!valid) { Reset(); }
        version = current;
        ready = true;
        Rebuilds++;
        return valid;
    }

    void Add(DurationListElement element)
    {
        int index = count++;
        elements[index] = element;
        next[index] = -1;
        if (element.Parent == null) { return; }
        ref Chain chain = ref CollectionsMarshal.GetValueRefOrAddDefault(chains, element.Parent, out bool exists);
        if (exists) { next[chain.Tail] = index; }
        else { chain.Head = index; }
        chain.Tail = index;
    }

    void EnsureCapacity(int required)
    {
        if (required <= elements.Length) { return; }
        int capacity = Math.Max(required, Math.Max(16, elements.Length * 2));
        Array.Resize(ref elements, capacity);
        Array.Resize(ref next, capacity);
    }

    public void Reset()
    {
        Array.Clear(elements, 0, count);
        count = 0;
        chains.Clear();
        ready = false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    DurationListElement ReferenceSingle(StatusEffect parent, ISerializableEntity target) =>
        source.Find(element => element.Parent == parent && element.Targets.FirstOrDefault() == target);

    [MethodImpl(MethodImplOptions.NoInlining)]
    DurationListElement ReferenceMany(StatusEffect parent, List<ISerializableEntity> targets) =>
        source.Find(element => element.Parent == parent && element.Targets.SequenceEqual(targets));
}
