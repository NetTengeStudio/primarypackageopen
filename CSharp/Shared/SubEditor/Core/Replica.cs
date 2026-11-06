using System;
using System.Buffers;
using System.Collections.Generic;

namespace PrimaryPackage.SubEditor;

public enum OfferResult : byte { Applied, Duplicate, Buffered, Overflow, Malformed, Failed }

public delegate bool FrameHandler(OpFrame.View frame);

/// <summary>
/// Client-side ordering. Applies frames strictly in revision order, buffers out-of-order ones (bounded),
/// reports the missing range for a resend request, and holds live frames back while the snapshot is still loading.
/// </summary>
public sealed class Replica
{
    public const int MaxPendingFrames = 4096;
    public const int MaxPendingBytes = 8 << 20;

    private readonly FrameHandler apply;
    private readonly FrameHandler ready;
    private readonly SortedList<ulong, byte[]> pending = new();
    private readonly Dictionary<ulong, int> lengths = new();
    private int pendingBytes;
    private long lastGapRequest;
    private bool gapRequested;
    private ulong appliedAtRequest;
    private ulong expected;

    public Replica(FrameHandler apply, FrameHandler ready = null) { this.apply = apply; this.ready = ready; }

    public ulong Applied { get; private set; }
    public bool Active { get; private set; }
    public int PendingFrames => pending.Count;
    public void Expect(ulong revision) { if (revision > expected) { expected = revision; } }

    /// <summary>Snapshot is fully applied; it represents the world exactly at cutRev.</summary>
    public bool BeginAt(ulong cutRev)
    {
        Applied = cutRev; Active = true;
        DropStale();
        return Drain();
    }

    /// <summary>Back to the loading state (restarted join). Buffered frames are discarded.</summary>
    public void Reset()
    {
        Active = false; Applied = 0; ClearPending(); gapRequested = false; expected = 0;
    }

    public OfferResult Offer(ReadOnlySpan<byte> frame)
    {
        if (!OpFrame.TryRead(frame, out OpFrame.View v)) { return OfferResult.Malformed; }
        Expect(v.Rev);
        if (Active && v.Rev <= Applied) { return OfferResult.Duplicate; }
        if (pending.ContainsKey(v.Rev)) { return OfferResult.Duplicate; }
        if (Active && v.Rev == Applied + 1 && (ready == null || ready(v)))
        {
            if (!apply(v)) { Active = false; return OfferResult.Failed; }
            Applied = v.Rev;
            return Drain() ? OfferResult.Applied : OfferResult.Failed;
        }
        if (pending.Count >= MaxPendingFrames || pendingBytes + frame.Length > MaxPendingBytes) { return OfferResult.Overflow; }
        byte[] copy = ArrayPool<byte>.Shared.Rent(frame.Length);
        frame.CopyTo(copy);
        pending.Add(v.Rev, copy); lengths[v.Rev] = frame.Length; pendingBytes += frame.Length;
        return OfferResult.Buffered;
    }

    /// <summary>
    /// If a hole exists in front of the buffered frames, returns it at most once per retryTicks while no progress was made; a repaired hole lets the next one be asked at once.
    /// While loading (not Active) no gap is reported: the snapshot's cut revision decides what is missing.
    /// </summary>
    public bool TryGetGap(long now, long retryTicks, out ulong from, out ulong to)
    {
        from = to = 0;
        if (!Active || expected <= Applied || (gapRequested && Applied == appliedAtRequest && now - lastGapRequest < retryTicks)) { return false; }
        from = Applied + 1; to = pending.Count == 0 ? expected : pending.Keys[0] - 1;
        if (to < from) { return false; }
        lastGapRequest = now; gapRequested = true; appliedAtRequest = Applied;
        return true;
    }

    private void DropStale()
    {
        while (pending.Count > 0 && pending.Keys[0] <= Applied) { Remove(0); }
    }

    /// <summary>Resume frames waiting for their native-event prerequisite without treating the wait as an apply failure.</summary>
    public bool Drain()
    {
        if (!Active) { return true; }
        while (pending.Count > 0 && pending.Keys[0] == Applied + 1)
        {
            ulong rev = pending.Keys[0];
            byte[] buf = pending.Values[0];
            int len = lengths[rev];
            if (!OpFrame.TryRead(buf.AsSpan(0, len), out OpFrame.View v)) { Active = false; return false; }
            if (ready != null && !ready(v)) { return true; }
            if (!apply(v)) { Active = false; return false; }
            Applied = rev;
            Remove(0);
        }
        return true;
    }

    private void Remove(int index)
    {
        ulong rev = pending.Keys[index];
        ArrayPool<byte>.Shared.Return(pending.Values[index]);
        pendingBytes -= lengths[rev]; lengths.Remove(rev);
        pending.RemoveAt(index);
    }

    private void ClearPending() { while (pending.Count > 0) { Remove(pending.Count - 1); } }
}
