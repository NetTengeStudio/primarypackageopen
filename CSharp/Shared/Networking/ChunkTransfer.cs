using System;
using System.Security.Cryptography;

namespace PrimaryPackage.Networking;

internal sealed class WorldBlob {
    internal const int ChunkBytes = 768, MaxBytes = 16 * 1024 * 1024, Window = 32;
    private readonly byte[] payload, digest;
    internal Guid Id { get; }
    internal int Length => payload.Length;
    internal int Count => (Length + ChunkBytes - 1) / ChunkBytes;
    internal ReadOnlySpan<byte> Digest => digest;
    // Ownership is transferred, not shared with a mutable serializer buffer.
    internal WorldBlob(Guid id, byte[] ownedPayload) {
        if (id == Guid.Empty || ownedPayload == null || ownedPayload.Length is < 1 or > MaxBytes) { throw new ArgumentException("Invalid world snapshot"); }
        Id = id; payload = ownedPayload; digest = SHA256.HashData(payload);
    }
    internal ReadOnlyMemory<byte> Chunk(int index) {
        if ((uint)index >= Count) { throw new ArgumentOutOfRangeException(nameof(index)); }
        int start = index * ChunkBytes; return payload.AsMemory(start, Math.Min(ChunkBytes, Length - start));
    }
}

/// <summary>Single-owner selective repeat with bounded congestion window and pacing.
/// Tick/ACK are called on the same thread. No allocations after construction.</summary>
internal sealed class ChunkSender {
    private struct Slot { internal int Index, Attempts; internal double First, Last; internal bool Acked; }
    private readonly Slot[] slots = new Slot[WorldBlob.Window];
    private readonly WorldBlob blob;
    private readonly int bytesPerSecond;
    private int first, next;
    private double clock, credit, progress, srtt, variance, rto = 1, window = 4;
    internal bool Complete => first == blob.Count;
    internal int AcknowledgedChunks => first;
    internal int Retransmissions { get; private set; }
    internal long SentBytes { get; private set; }
    internal double RetransmitTimeout => rto;
    internal ChunkSender(WorldBlob blob, int bytesPerSecond, double now) {
        if (blob == null || bytesPerSecond is < 2048 or > 262144 || !double.IsFinite(now)) { throw new ArgumentException("Invalid snapshot sender"); }
        this.blob = blob; this.bytesPerSecond = bytesPerSecond; clock = progress = now;
        credit = Math.Min(bytesPerSecond, WorldBlob.ChunkBytes * 4);
    }
    internal bool TryNext(double now, out int index, out ReadOnlyMemory<byte> bytes) {
        Advance(now); index = -1; bytes = default;
        if (Complete || credit < WorldBlob.ChunkBytes + ChunkWire.MaxHeaderBytes) { return false; }
        for (int i = first; i < next; i++) {
            ref Slot pending = ref slots[i % WorldBlob.Window];
            double timeout = Math.Min(8, rto * (1 << Math.Min(3, pending.Attempts - 1)));
            if (pending.Acked || now - pending.Last < timeout) { continue; }
            pending.Last = now; if (pending.Attempts < int.MaxValue) { pending.Attempts++; }
            Retransmissions++; window = Math.Max(2, window * .5);
            if (srtt == 0) { rto = Math.Min(8, rto * 2); }
            index = i; break;
        }
        if (index < 0) {
            if (next >= blob.Count || next - first >= Math.Min(WorldBlob.Window, (int)window)) { return false; }
            index = next++; slots[index % WorldBlob.Window] = new Slot { Index = index, First = now, Last = now, Attempts = 1 };
        }
        bytes = blob.Chunk(index); int cost = bytes.Length + ChunkWire.MaxHeaderBytes;
        credit -= cost; SentBytes += cost; return true;
    }
    internal void Acknowledge(int prefix, ulong following, double now) {
        Advance(now);
        if (prefix < 0 || prefix > next || prefix > blob.Count) { throw new ArgumentException("ACK names unsent snapshot data"); }
        // Validate the whole ACK before changing any slot, including contradictory suffix bits.
        for (int bit = 0; bit < 64; bit++) {
            if ((following & (1UL << bit)) != 0 && (prefix + bit >= next || prefix + bit >= blob.Count)) { throw new ArgumentException("ACK bitmap names unsent snapshot data"); }
        }
        bool changed = false;
        for (int i = first; i < next; i++) {
            bool acknowledged = i < prefix || i >= prefix && i - prefix < 64 && (following & (1UL << (i - prefix))) != 0;
            if (!acknowledged) { continue; }
            ref Slot slot = ref slots[i % WorldBlob.Window];
            if (slot.Acked) { continue; }
            changed = slot.Acked = true;
            if (slot.Attempts == 1) {
                double sample = Math.Max(.001, now - slot.First);
                if (srtt == 0) { srtt = sample; variance = sample * .5; }
                else { variance = .75 * variance + .25 * Math.Abs(srtt - sample); srtt = .875 * srtt + .125 * sample; }
                rto = Math.Clamp(srtt + 4 * variance, .2, 8);
            }
            window = Math.Min(WorldBlob.Window, window + 1 / window);
        }
        while (first < next && slots[first % WorldBlob.Window].Acked) { slots[first % WorldBlob.Window] = default; first++; }
        if (changed) { progress = now; }
    }
    internal bool Expired(double now) { Advance(now); return !Complete && now - progress >= 60; }
    private void Advance(double now) {
        if (!double.IsFinite(now) || now < clock) { throw new ArgumentException("Snapshot clock must be monotonic"); }
        credit = Math.Min(WorldBlob.ChunkBytes * WorldBlob.Window, credit + (now - clock) * bytesPerSecond); clock = now;
    }
}

internal sealed class ChunkReceiver : IDisposable {
    private byte[] payload;
    private readonly byte[] digest;
    private readonly ulong[] received;
    private int first, count;
    private double clock, progress;
    internal Guid Id { get; }
    internal int Length { get; }
    internal int ChunkCount { get; }
    internal bool Complete { get; private set; }
    internal bool Faulted { get; private set; }
    internal int ReceivedChunks => count;
    internal int Prefix => first;
    internal ulong Following {
        get { ulong mask = 0; for (int bit = 0; bit < 64 && first + bit < ChunkCount; bit++) { if (Has(first + bit)) { mask |= 1UL << bit; } } return mask; }
    }
    internal ChunkReceiver(Guid id, int length, ReadOnlySpan<byte> hash, double now) {
        if (id == Guid.Empty || length is < 1 or > WorldBlob.MaxBytes || hash.Length != 32 || !double.IsFinite(now)) { throw new ArgumentException("Invalid snapshot offer"); }
        Id = id; Length = length; ChunkCount = (length + WorldBlob.ChunkBytes - 1) / WorldBlob.ChunkBytes;
        payload = new byte[length]; digest = hash.ToArray(); received = new ulong[(ChunkCount + 63) / 64]; clock = progress = now;
    }
    internal bool Receive(int index, ReadOnlySpan<byte> bytes, double now) {
        Advance(now);
        if (Faulted || payload == null) { throw new InvalidOperationException("Snapshot receiver is closed"); }
        if ((uint)index >= ChunkCount || bytes.Length != Math.Min(WorldBlob.ChunkBytes, Length - index * WorldBlob.ChunkBytes)) { throw new ArgumentException("Invalid snapshot chunk bounds"); }
        Span<byte> target = payload.AsSpan(index * WorldBlob.ChunkBytes, bytes.Length);
        if (Has(index)) { if (!bytes.SequenceEqual(target)) { Fault(); throw new ArgumentException("Snapshot duplicate has different contents"); } return false; }
        bytes.CopyTo(target); received[index >> 6] |= 1UL << (index & 63); count++; progress = now;
        while (first < ChunkCount && Has(first)) { first++; }
        if (count == ChunkCount) {
            Span<byte> actual = stackalloc byte[32]; SHA256.HashData(payload, actual);
            if (!CryptographicOperations.FixedTimeEquals(actual, digest)) { Fault(); throw new ArgumentException("Snapshot integrity check failed"); }
            Complete = true;
        }
        return true;
    }
    // Borrow until Dispose. Consumers must finish parsing before releasing the receiver.
    internal ReadOnlyMemory<byte> Payload => Complete && payload != null && !Faulted ? payload : throw new InvalidOperationException("Snapshot is not complete");
    internal bool Expired(double now) { Advance(now); return !Complete && now - progress >= 60; }
    private bool Has(int index) => (received[index >> 6] & (1UL << (index & 63))) != 0;
    private void Advance(double now) { if (!double.IsFinite(now) || now < clock) { throw new ArgumentException("Snapshot clock must be monotonic"); } clock = now; }
    private void Fault() { Faulted = true; Dispose(); }
    public void Dispose() { if (payload != null) { Array.Clear(payload, 0, payload.Length); payload = null; } }
}
