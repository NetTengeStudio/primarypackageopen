using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Lights;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace IEngine.Render;

sealed class ShadowIndex
{
    struct Entry
    {
        public ConvexHull Hull;
        public Rectangle Bounds;
        public int Position;
    }

    struct Node
    {
        public Vector2 Minimum, Maximum;
        public int Parent, Escape, Position;
    }

    sealed class EntryOrder : IComparer<Entry>
    {
        public static readonly EntryOrder Horizontal = new(true), Vertical = new(false);
        readonly bool horizontal;
        EntryOrder(bool horizontal) => this.horizontal = horizontal;
        public int Compare(Entry left, Entry right) => horizontal
            ? ((double)left.Bounds.X * 2 + left.Bounds.Width).CompareTo((double)right.Bounds.X * 2 + right.Bounds.Width)
            : ((double)left.Bounds.Y * 2 + left.Bounds.Height).CompareTo((double)right.Bounds.Y * 2 + right.Bounds.Height);
    }

    static ConditionalWeakTable<List<ConvexHull>, ShadowIndex> indices = new();
    static readonly AccessTools.FieldRef<List<ConvexHull>, int> Version = AccessTools.FieldRefAccess<List<ConvexHull>, int>("_version");
    readonly Dictionary<ConvexHull, int> leaves = new(ReferenceEqualityComparer.Instance);
    Entry[] entries = Array.Empty<Entry>();
    Node[] nodes = Array.Empty<Node>();
    ulong[] matches = Array.Empty<ulong>();
    int version, used;
    bool supported;
    public static long Builds, Refits, Queries, Tested, Rejected;

    static ShadowIndex Create(List<ConvexHull> source) => new() { version = unchecked(Version(source) - 1) };
    public static void Clear() => indices = new();

    public static void Changed(ConvexHull hull)
    {
        foreach (ConvexHullList group in ConvexHull.HullLists)
        {
            List<ConvexHull> source = group.List;
            if (indices.TryGetValue(source, out ShadowIndex index) && index.version == Version(source) && index.leaves.TryGetValue(hull, out int leaf))
            {
                index.Refit(leaf, hull.BoundingBox);
            }
        }
    }

    void Refit(int leaf, Rectangle bounds)
    {
        if (bounds.Width < 0 || bounds.Height < 0) { supported = false; }
        Vector2 minimum = new(bounds.X, bounds.Y), maximum = new((float)bounds.X + bounds.Width, (float)bounds.Y + bounds.Height);
        if (nodes[leaf].Minimum == minimum && nodes[leaf].Maximum == maximum) { return; }
        nodes[leaf].Minimum = minimum;
        nodes[leaf].Maximum = maximum;
        int parent = nodes[leaf].Parent;
        while (parent >= 0)
        {
            int left = parent + 1, right = nodes[left].Escape;
            minimum = Vector2.Min(nodes[left].Minimum, nodes[right].Minimum);
            maximum = Vector2.Max(nodes[left].Maximum, nodes[right].Maximum);
            if (nodes[parent].Minimum == minimum && nodes[parent].Maximum == maximum) { break; }
            nodes[parent].Minimum = minimum;
            nodes[parent].Maximum = maximum;
            parent = nodes[parent].Parent;
        }
        Refits++;
    }

    void Build(List<ConvexHull> source)
    {
        int count = source.Count;
        if (entries.Length < count)
        {
            int capacity = Math.Max(count, Math.Max(32, entries.Length * 2));
            entries = new Entry[capacity];
            matches = new ulong[(capacity + 63) >> 6];
            nodes = new Node[checked(capacity * 2)];
        }
        leaves.Clear();
        supported = true;
        for (int i = 0; i < count; i++) { entries[i] = new Entry { Hull = source[i], Bounds = source[i].BoundingBox, Position = i }; }
        used = 0;
        if (count > 0) { Build(0, count, -1); }
        Array.Clear(entries, 0, count);
        version = Version(source);
        Builds++;
    }

    int Build(int start, int count, int parent)
    {
        int node = used++;
        Rectangle first = entries[start].Bounds;
        Vector2 minimum = new(first.X, first.Y), maximum = new((float)first.X + first.Width, (float)first.Y + first.Height);
        for (int i = start + 1; i < start + count; i++)
        {
            Rectangle bounds = entries[i].Bounds;
            minimum = Vector2.Min(minimum, new Vector2(bounds.X, bounds.Y));
            maximum = Vector2.Max(maximum, new Vector2((float)bounds.X + bounds.Width, (float)bounds.Y + bounds.Height));
        }
        nodes[node] = new Node { Minimum = minimum, Maximum = maximum, Parent = parent, Position = -1 };
        if (count == 1)
        {
            Entry entry = entries[start];
            nodes[node].Position = entry.Position;
            if (!leaves.TryAdd(entry.Hull, node) || entry.Bounds.Width < 0 || entry.Bounds.Height < 0) { supported = false; }
        }
        else
        {
            Array.Sort(entries, start, count, maximum.X - minimum.X >= maximum.Y - minimum.Y ? EntryOrder.Horizontal : EntryOrder.Vertical);
            int left = count / 2;
            Build(start, left, node);
            Build(start + left, count - left, node);
        }
        nodes[node].Escape = used;
        return node;
    }

    public static void Append(List<ConvexHull> source, Vector2 position, float range, List<ConvexHull> destination)
    {
        if (source.Count < 32 || !float.IsFinite(range) || range < 0 || !float.IsFinite(position.X) || !float.IsFinite(position.Y))
        {
            if (source.Count < 32) { indices.Remove(source); }
            for (int i = 0; i < source.Count; i++) { if (MathUtils.CircleIntersectsRectangle(position, range, source[i].BoundingBox)) { destination.Add(source[i]); } }
            return;
        }
        ShadowIndex index = indices.GetValue(source, Create);
        if (index.version != Version(source)) { index.Build(source); }
        if (!index.supported)
        {
            for (int i = 0; i < source.Count; i++) { if (MathUtils.CircleIntersectsRectangle(position, range, source[i].BoundingBox)) { destination.Add(source[i]); } }
            return;
        }
        Queries++;
        int words = (source.Count + 63) >> 6;
        Array.Clear(index.matches, 0, words);
        int node = 0, tested = 0, rejected = 0;
        float left = position.X - range, right = position.X + range, top = position.Y - range, bottom = position.Y + range;
        while (node < index.used)
        {
            ref Node bounds = ref index.nodes[node];
            if (right < bounds.Minimum.X || left > bounds.Maximum.X || bottom < bounds.Minimum.Y || top > bounds.Maximum.Y)
            {
                rejected++;
                node = bounds.Escape;
                continue;
            }
            int positionInSource = bounds.Position;
            if (positionInSource >= 0)
            {
                tested++;
                if (MathUtils.CircleIntersectsRectangle(position, range, source[positionInSource].BoundingBox)) { index.matches[positionInSource >> 6] |= 1UL << positionInSource; }
            }
            node++;
        }
        Tested += tested;
        Rejected += rejected;
        for (int i = 0; i < words; i++)
        {
            ulong bits = index.matches[i];
            while (bits != 0)
            {
                destination.Add(source[(i << 6) + BitOperations.TrailingZeroCount(bits)]);
                bits &= bits - 1;
            }
        }
    }
}
