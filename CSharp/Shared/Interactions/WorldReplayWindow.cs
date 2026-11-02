using System;

namespace PrimaryPackage.Interactions;

// Application acknowledgements bound the reliable transport queue independently of RTT.
// One slot owns a complete operation, never a fragment of a multi-object edit.
internal sealed class WorldReplayWindow {
    internal const int Capacity = 16, ByteCapacity = 128 * 1024;
    private readonly (ulong Sequence, int Bytes)[] outstanding = new (ulong, int)[Capacity];
    private readonly int rate;
    private int first, count, bytes;
    private double clock, credit, progress;
    internal ulong Acknowledged { get; private set; }
    internal ulong Sent { get; private set; }
    internal int Count => count;
    internal int Bytes => bytes;
    internal WorldReplayWindow(ulong cut, int bytesPerSecond, double now) {
        if (bytesPerSecond is < 2048 or > 262144 || !double.IsFinite(now) || now < 0) { throw new ArgumentOutOfRangeException(); }
        rate = bytesPerSecond; clock = progress = now; credit = Math.Min(rate, ByteCapacity); Acknowledged = Sent = cut;
    }
    private void Advance(double now) {
        if (!double.IsFinite(now) || now < clock) { throw new ArgumentOutOfRangeException(nameof(now)); }
        credit = Math.Min(ByteCapacity, credit + (now - clock) * rate); clock = now;
    }
    internal bool CanSend(int size, double now) {
        if (size is < 1 or > ByteCapacity) { throw new ArgumentOutOfRangeException(nameof(size)); }
        Advance(now); return count < Capacity && size <= ByteCapacity - bytes && size <= credit;
    }
    internal void Enqueue(ulong sequence, int size, double now) {
        if (sequence <= Sent || !CanSend(size, now)) { throw new InvalidOperationException("Replay application window is full or out of order"); }
        if (count == 0) { progress = now; }
        outstanding[(first + count) % Capacity] = (sequence, size); count++; bytes += size; credit -= size; Sent = sequence;
    }
    internal bool Acknowledge(ulong sequence, double now) {
        Advance(now);
        if (sequence <= Acknowledged) { return false; }
        int release = -1;
        for (int i = 0; i < count; i++) { if (outstanding[(first + i) % Capacity].Sequence == sequence) { release = i + 1; break; } }
        if (release < 0) { throw new ArgumentException("Acknowledgement refers to an unsent world operation"); }
        for (int i = 0; i < release; i++) { bytes -= outstanding[first].Bytes; outstanding[first] = default; first = (first + 1) % Capacity; }
        count -= release; Acknowledged = sequence; progress = now; return true;
    }
    internal bool Expired(double now) { Advance(now); return count > 0 && now - progress >= 60; }
}
