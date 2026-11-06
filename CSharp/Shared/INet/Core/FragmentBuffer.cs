using System;
using System.Collections.Generic;

namespace INet;

public enum FragmentResult : byte { Pending, Complete, Duplicate, Rejected }

public sealed class FragmentBuffer
{
    public const int FragmentBytes = 1100;
    public const int MaxMessageBytes = 8 << 20;
    public const int MaxBufferedBytes = 16 << 20;
    public const int MaxPending = 64, ReplayWindow = 256;
    public const long ExpireMs = 60000;
    private sealed class Pending(int count, long now)
    {
        public readonly byte[] Data = new byte[count * FragmentBytes];
        public readonly ulong[] Received = new ulong[(count + 63) / 64];
        public readonly int Count = count;
        public readonly long Started = now;
        public int Missing = count, Length;
    }
    private readonly Dictionary<ushort, Pending> pending = new();
    private readonly Dictionary<ushort, long> completed = new();
    private readonly Queue<ushort> order = new();
    private readonly List<ushort> expired = new();
    private long nextExpiry = long.MaxValue;
    public int BufferedBytes { get; private set; }
    public int PendingCount => pending.Count;

    public FragmentResult Add(ushort messageId, int index, int count, ReadOnlySpan<byte> data, long now, out byte[] message)
    {
        message = null;
        Prune(now);
        if (count <= 0 || count > (MaxMessageBytes + FragmentBytes - 1) / FragmentBytes || (uint)index >= (uint)count ||
            data.IsEmpty || data.Length > FragmentBytes || index < count - 1 && data.Length != FragmentBytes ||
            index == count - 1 && (long)index * FragmentBytes + data.Length > MaxMessageBytes) { return FragmentResult.Rejected; }
        if (completed.ContainsKey(messageId)) { return FragmentResult.Duplicate; }
        if (!pending.TryGetValue(messageId, out Pending transfer))
        {
            int bytes = count * FragmentBytes;
            if (pending.Count >= MaxPending || bytes > MaxBufferedBytes - BufferedBytes) { return FragmentResult.Rejected; }
            pending.Add(messageId, transfer = new Pending(count, now));
            nextExpiry = Math.Min(nextExpiry, now + ExpireMs);
            BufferedBytes += bytes;
        }
        else if (transfer.Count != count) { Remove(messageId, transfer); return FragmentResult.Rejected; }
        ulong bit = 1UL << (index & 63);
        Span<byte> destination = transfer.Data.AsSpan(index * FragmentBytes, data.Length);
        if ((transfer.Received[index >> 6] & bit) != 0)
        {
            if (destination.SequenceEqual(data) && (index != count - 1 || transfer.Length == index * FragmentBytes + data.Length)) { return FragmentResult.Duplicate; }
            Remove(messageId, transfer);
            return FragmentResult.Rejected;
        }
        data.CopyTo(destination);
        transfer.Received[index >> 6] |= bit;
        if (index == count - 1) { transfer.Length = index * FragmentBytes + data.Length; }
        if (--transfer.Missing != 0) { return FragmentResult.Pending; }
        message = transfer.Length == transfer.Data.Length ? transfer.Data : transfer.Data.AsSpan(0, transfer.Length).ToArray();
        Remove(messageId, transfer);
        completed.Add(messageId, now); order.Enqueue(messageId);
        while (order.Count > ReplayWindow) { completed.Remove(order.Dequeue()); }
        return FragmentResult.Complete;
    }

    private void Remove(ushort id, Pending transfer) { pending.Remove(id); BufferedBytes -= transfer.Data.Length; }

    private void Prune(long now)
    {
        if (now >= nextExpiry)
        {
            nextExpiry = long.MaxValue;
            foreach (var pair in pending)
            {
                if (now - pair.Value.Started >= ExpireMs) { expired.Add(pair.Key); }
                else { nextExpiry = Math.Min(nextExpiry, pair.Value.Started + ExpireMs); }
            }
            foreach (ushort id in expired) { Remove(id, pending[id]); }
            expired.Clear();
        }
        while (order.Count != 0 && now - completed[order.Peek()] >= ExpireMs) { completed.Remove(order.Dequeue()); }
    }
}
