using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace INet;

public readonly struct EventKey : IEquatable<EventKey>
{
    public readonly object Owner;
    public readonly Type Kind;
    public readonly object Identity;

    public EventKey(object owner, Type kind, object identity)
    {
        Owner = owner;
        Kind = kind;
        Identity = identity;
    }

    public bool Equals(EventKey other) => ReferenceEquals(Owner, other.Owner) && ReferenceEquals(Kind, other.Kind) && ReferenceEquals(Identity, other.Identity);
    public override bool Equals(object obj) => obj is EventKey k && Equals(k);
    public override int GetHashCode()
    {
        unchecked
        {
            int h = RuntimeHelpers.GetHashCode(Owner);
            h = (h * 397) ^ RuntimeHelpers.GetHashCode(Kind);
            return (h * 397) ^ RuntimeHelpers.GetHashCode(Identity);
        }
    }
}

public sealed class EventDedupIndex<T> where T : class
{
    struct Entry
    {
        public T Single;
        public List<T> Multiple;
    }

    readonly Dictionary<EventKey, Entry> buckets = new(1024);
    readonly Stack<List<T>> pool = new();
    int count;

    public int Count => count;
    public int Buckets => buckets.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(in EventKey key, T item)
    {
        ref Entry entry = ref CollectionsMarshal.GetValueRefOrAddDefault(buckets, key, out bool exists);
        if (!exists) { entry.Single = item; }
        else if (entry.Multiple != null) { entry.Multiple.Add(item); }
        else { Promote(ref entry, item); }
        count++;
    }

    void Promote(ref Entry entry, T item)
    {
        List<T> list = pool.Count > 0 ? pool.Pop() : new List<T>(2);
        list.Add(entry.Single);
        list.Add(item);
        entry.Multiple = list;
        entry.Single = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> Bucket(in EventKey key)
    {
        ref Entry entry = ref CollectionsMarshal.GetValueRefOrNullRef(buckets, key);
        if (Unsafe.IsNullRef(ref entry)) { return ReadOnlySpan<T>.Empty; }
        return entry.Multiple == null
            ? MemoryMarshal.CreateReadOnlySpan(ref entry.Single, 1)
            : CollectionsMarshal.AsSpan(entry.Multiple);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(in EventKey key, T item)
    {
        ref Entry entry = ref CollectionsMarshal.GetValueRefOrNullRef(buckets, key);
        if (Unsafe.IsNullRef(ref entry)) { return false; }
        List<T> list = entry.Multiple;
        if (list == null)
        {
            if (!ReferenceEquals(entry.Single, item)) { return false; }
            buckets.Remove(key);
        }
        else
        {
            int i = 0;
            while (i < list.Count && !ReferenceEquals(list[i], item)) { i++; }
            if (i == list.Count) { return false; }
            list.RemoveAt(i);
            if (list.Count == 1) { Demote(ref entry, list); }
        }
        count--;
        return true;
    }

    void Demote(ref Entry entry, List<T> list)
    {
        entry.Single = list[0];
        entry.Multiple = null;
        list.Clear();
        pool.Push(list);
    }

    public void Clear()
    {
        foreach (Entry entry in buckets.Values)
        {
            if (entry.Multiple == null) { continue; }
            entry.Multiple.Clear();
            pool.Push(entry.Multiple);
        }
        buckets.Clear();
        count = 0;
    }
}