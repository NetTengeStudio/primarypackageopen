using System;
using System.Collections.Generic;

namespace PrimaryPackage.Interactions;

/// <summary>Submarine-local broad phase. Large rectangles do not explode cell membership.
/// Results are exact AABB intersections; the caller owns narrow-phase picking and ordering.</summary>
internal sealed class BoundsIndex<T> where T : class {
    private const float CellSize = 256;
    private const int MaxCells = 256;
    private readonly record struct Bounds(float Left, float Bottom, float Right, float Top) {
        internal bool Intersects(in Bounds other) => Left <= other.Right && Right >= other.Left && Bottom <= other.Top && Top >= other.Bottom;
    }
    private readonly record struct Cells(int Left, int Bottom, int Right, int Top) {
        internal long Count => ((long)Right - Left + 1) * ((long)Top - Bottom + 1);
    }
    private readonly Dictionary<T, Bounds> entries = new();
    private readonly Dictionary<(int X, int Y), HashSet<T>> cells = new();
    // Buckets own their buffers. Empty buckets retain no entity references and can be reused
    // across cell crossings; exceptionally large buffers are allowed to become collectible.
    private readonly Stack<HashSet<T>> spare = new();
    private readonly HashSet<T> large = new(), visited = new();
    internal int Count => entries.Count;
    private static int Cell(float value) => (int)MathF.Floor(value / CellSize);
    private static Cells Range(in Bounds bounds) => new(Cell(bounds.Left), Cell(bounds.Bottom), Cell(bounds.Right), Cell(bounds.Top));
    private static Bounds Checked(float left, float bottom, float right, float top) {
        if (!float.IsFinite(left) || !float.IsFinite(bottom) || !float.IsFinite(right) || !float.IsFinite(top) ||
            left > right || bottom > top || Math.Abs(left) > 1e8f || Math.Abs(right) > 1e8f || Math.Abs(bottom) > 1e8f || Math.Abs(top) > 1e8f) { throw new ArgumentOutOfRangeException(nameof(left)); }
        return new(left, bottom, right, top);
    }
    internal void Upsert(T entity, float left, float bottom, float right, float top) {
        var bounds = Checked(left, bottom, right, top); Cells next = Range(bounds);
        if (entries.TryGetValue(entity, out Bounds before)) {
            entries[entity] = bounds;
            if (Range(before) == next) { return; }
            Unlink(entity, Range(before));
        } else { entries.Add(entity, bounds); }
        if (next.Count > MaxCells) { large.Add(entity); return; }
        for (int y = next.Bottom; y <= next.Top; y++) {
            for (int x = next.Left; x <= next.Right; x++) {
                if (!cells.TryGetValue((x, y), out var bucket)) { cells.Add((x, y), bucket = spare.Count > 0 ? spare.Pop() : new()); }
                bucket.Add(entity);
            }
        }
    }
    internal void Remove(T entity) { if (entries.Remove(entity, out Bounds before)) { Unlink(entity, Range(before)); } }
    private void Unlink(T entity, in Cells range) {
        if (range.Count > MaxCells) { large.Remove(entity); return; }
        for (int y = range.Bottom; y <= range.Top; y++) {
            for (int x = range.Left; x <= range.Right; x++) {
                if (cells.TryGetValue((x, y), out var bucket)) {
                    bucket.Remove(entity);
                    if (bucket.Count == 0) {
                        cells.Remove((x, y));
                        if (spare.Count < MaxCells && bucket.EnsureCapacity(0) <= 1024) { spare.Push(bucket); }
                    }
                }
            }
        }
    }
    internal void Query(float left, float bottom, float right, float top, List<T> results) {
        Bounds query = Checked(left, bottom, right, top); Cells range = Range(query); visited.Clear();
        if (range.Count > MaxCells * 16) {
            // Wide marquee: a single scan is cheaper than traversing a huge empty grid.
            foreach (var entry in entries) { if (entry.Value.Intersects(query)) { results.Add(entry.Key); } }
            return;
        }
        for (int y = range.Bottom; y <= range.Top; y++) {
            for (int x = range.Left; x <= range.Right; x++) {
                if (!cells.TryGetValue((x, y), out var bucket)) { continue; }
                foreach (T entity in bucket) { if (visited.Add(entity) && entries[entity].Intersects(query)) { results.Add(entity); } }
            }
        }
        foreach (T entity in large) { if (entries[entity].Intersects(query)) { results.Add(entity); } }
    }
}
