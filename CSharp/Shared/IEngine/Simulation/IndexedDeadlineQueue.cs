using System;

namespace IEngine.Simulation;

// Intrusive indexed binary min-heap: one deadline per entity slot.
// Set/Remove/TakeExpired: O(log N); Clear: O(N); memory O(live deadlines).
// Repeated wake calls update a heap key instead of allocating stale entries.
internal sealed class IndexedDeadlineQueue
{
    private int[] heap = Array.Empty<int>();
    private int[] positions = Array.Empty<int>();
    private double[] deadlines = Array.Empty<double>();
    private int count;

    internal int Count => count;

    internal bool Contains(int slot)
        => (uint)slot < (uint)positions.Length && positions[slot] >= 0;

    internal void Set(int slot, double until)
    {
        if (slot < 0 || double.IsNaN(until) || double.IsInfinity(until))
        {
            throw new ArgumentOutOfRangeException(nameof(until), "Deadline requires a valid slot and finite time.");
        }

        EnsureSlot(slot);
        deadlines[slot] = until;
        int i = positions[slot];
        if (i < 0)
        {
            EnsureHeap(count + 1);
            i = count++;
            heap[i] = slot;
            positions[slot] = i;
        }

        Rebalance(i);
    }

    internal bool Remove(int slot)
    {
        if (!Contains(slot)) { return false; }

        int index = positions[slot];
        positions[slot] = -1;
        int lastIndex = --count;

        if (index != lastIndex)
        {
            int moved = heap[lastIndex];
            heap[index] = moved;
            positions[moved] = index;
            Rebalance(index);
        }

        heap[lastIndex] = 0;
        return true;
    }

    internal bool TryTakeExpired(double now, out int slot)
    {
        if (count == 0 || deadlines[heap[0]] > now)
        {
            slot = -1;
            return false;
        }

        slot = heap[0];
        Remove(slot);
        return true;
    }

    internal void Clear()
    {
        for (int i = 0; i < count; i++)
        {
            positions[heap[i]] = -1;
            heap[i] = 0;
        }
        count = 0;
    }

    private void EnsureSlot(int slot)
    {
        if (slot < positions.Length) { return; }
        int old = positions.Length;
        int size = Math.Max(slot + 1, Math.Max(16, old * 2));
        Array.Resize(ref positions, size);
        Array.Resize(ref deadlines, size);
        Array.Fill(positions, -1, old, size - old);
    }

    private void EnsureHeap(int capacity)
    {
        if (capacity <= heap.Length) { return; }
        Array.Resize(ref heap, Math.Max(capacity, Math.Max(16, heap.Length * 2)));
    }

    private void Rebalance(int index)
    {
        if (index > 0 && ComesBefore(heap[index], heap[(index - 1) >> 1]))
        {
            Rise(index);
        }
        else
        {
            Sink(index);
        }
    }

    private bool ComesBefore(int a, int b)
        => deadlines[a] < deadlines[b] || (deadlines[a] == deadlines[b] && a < b);

    private void Swap(int a, int b)
    {
        int sa = heap[a], sb = heap[b];
        heap[a] = sb;
        heap[b] = sa;
        positions[sa] = b;
        positions[sb] = a;
    }

    private void Rise(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) >> 1;
            if (!ComesBefore(heap[index], heap[parent])) { break; }
            Swap(index, parent);
            index = parent;
        }
    }

    private void Sink(int index)
    {
        while (true)
        {
            int child = (index << 1) + 1;
            if (child >= count) { return; }
            if (child + 1 < count && ComesBefore(heap[child + 1], heap[child])) { child++; }
            if (!ComesBefore(heap[child], heap[index])) { return; }
            Swap(index, child);
            index = child;
        }
    }
}
