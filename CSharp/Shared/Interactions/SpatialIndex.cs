using System;
using System.Collections.Generic;

namespace PrimaryPackage.Interactions;

public sealed class SpatialIndex<T> where T : class {
    private readonly struct Entry {
        public readonly long Cell;
        public readonly int Slot;
        public Entry(long cell, int slot) { Cell = cell; Slot = slot; }
    }
    private readonly Dictionary<long, List<T>> cells = new();
    private readonly Dictionary<T, Entry> entries = new();
    private readonly float cellSize;
    public int Count => entries.Count;
    public SpatialIndex(float cellSize = 512) {
        if (!float.IsFinite(cellSize) || cellSize <= 0) { throw new ArgumentOutOfRangeException(nameof(cellSize)); }
        this.cellSize = cellSize;
    }
    private int Cell(float value) {
        double cell = Math.Floor(value / cellSize);
        if (!double.IsFinite(cell) || cell < int.MinValue || cell > int.MaxValue) { throw new ArgumentOutOfRangeException(nameof(value)); }
        return (int)cell;
    }
    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
    public void Upsert(T value, float x, float y) {
        long key = Key(Cell(x), Cell(y));
        if (entries.TryGetValue(value, out var previous)) {
            if (previous.Cell == key) { return; }
            Remove(value);
        }
        if (!cells.TryGetValue(key, out var bucket)) { bucket = new List<T>(); cells.Add(key, bucket); }
        entries.Add(value, new Entry(key, bucket.Count));
        bucket.Add(value);
    }
    public bool Remove(T value) {
        if (!entries.Remove(value, out var entry)) { return false; }
        List<T> bucket = cells[entry.Cell];
        T last = bucket[bucket.Count - 1];
        if (entry.Slot != bucket.Count - 1) { bucket[entry.Slot] = last; entries[last] = new Entry(entry.Cell, entry.Slot); }
        bucket.RemoveAt(bucket.Count - 1);
        if (bucket.Count == 0) { cells.Remove(entry.Cell); }
        return true;
    }
    public void Query(float x, float y, float radius, List<T> destination) {
        if (!float.IsFinite(radius) || radius < 0 || radius > cellSize * 64) { throw new ArgumentOutOfRangeException(nameof(radius)); }
        int minX = Cell(x - radius), maxX = Cell(x + radius), minY = Cell(y - radius), maxY = Cell(y + radius);
        for (long cy = minY; cy <= maxY; cy++) {
            for (long cx = minX; cx <= maxX; cx++) {
                if (cells.TryGetValue(Key((int)cx, (int)cy), out var bucket)) { destination.AddRange(bucket); }
            }
        }
    }
    public void Clear() { cells.Clear(); entries.Clear(); }
}
