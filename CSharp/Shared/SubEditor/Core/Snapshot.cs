using System;

namespace PrimaryPackage.SubEditor;

public enum CursorPhase : byte { Initial, Settling, Done, Failed }

/// <summary>
/// Incremental ("fuzzy") snapshot. The world is never frozen and never serialized in one tick:
/// entities are serialized in budgeted batches, each stamped with the version it was read at.
/// After the initial pass, every entity changed since its stamp is re-sent; when the changed set is small enough
/// the last batch is serialized synchronously and the cut revision is taken in the same tick, so the result
/// equals the world exactly at that revision. Edits arriving during the stream cost only the re-send of what they touched.
/// </summary>
public sealed class SnapshotCursor
{
    private readonly EntityVersions versions;
    private readonly ushort[] order;
    private readonly ulong[] stamp = new ulong[EntityVersions.Capacity];
    private readonly ushort[] queue;
    private int next, queued, queueHead, rounds;

    public SnapshotCursor(EntityVersions versions, ushort[] orderedIds, int tailLimit = 64, int maxRounds = 8)
    {
        this.versions = versions; order = orderedIds; TailLimit = tailLimit; MaxRounds = maxRounds;
        queue = new ushort[EntityVersions.Capacity];
    }

    public int TailLimit { get; }
    public int MaxRounds { get; }
    public CursorPhase Phase { get; private set; }
    public int Sent { get; private set; }
    public int InitialCount => order.Length;
    /// <summary>True when the batch just returned is the last one and must be serialized in the current tick.</summary>
    public bool IsFinalBatch { get; private set; }

    /// <summary>Fills dest with ids to serialize. dest.Length must be at least TailLimit.</summary>
    public int Next(Span<ushort> dest)
    {
        if (dest.Length < TailLimit) { throw new ArgumentException("batch buffer smaller than TailLimit", nameof(dest)); }
        IsFinalBatch = false;
        int n = 0;
        if (Phase == CursorPhase.Initial)
        {
            while (n < dest.Length && next < order.Length) { ushort id = order[next++]; stamp[id] = versions.Latest(id); dest[n++] = id; }
            if (next >= order.Length) { Phase = CursorPhase.Settling; }
            Sent += n;
            return n;
        }
        if (Phase != CursorPhase.Settling) { return 0; }
        // A final batch is only legitimate right after a fresh scan: the cut is taken in this same tick,
        // so nothing may have changed between the scan and the cut. Leftovers of an older scan prove nothing.
        if (queued - queueHead <= TailLimit && !Rescan()) { return 0; }
        int remaining = queued - queueHead;
        bool final = remaining <= TailLimit;
        int take = final ? remaining : Math.Min(remaining, dest.Length);
        for (int i = 0; i < take; i++) { ushort id = queue[queueHead++]; stamp[id] = versions.Latest(id); dest[n++] = id; }
        IsFinalBatch = final;
        Sent += n;
        return n;
    }

    /// <summary>Call after the final batch was serialized, in the same tick, with Authority.Rev.</summary>
    public ulong Complete(ulong currentRev)
    {
        Phase = CursorPhase.Done;
        return currentRev;
    }

    private bool Rescan()
    {
        if (++rounds > MaxRounds) { Phase = CursorPhase.Failed; return false; }
        queueHead = queued = 0;
        for (int id = 0; id < EntityVersions.Capacity; id++)
        {
            if (versions.Latest((ushort)id) > stamp[id]) { queue[queued++] = (ushort)id; }
        }
        // Nothing changed: still emit a final, empty batch so the caller takes the cut in this tick.
        return true;
    }
}

/// <summary>
/// Sender-side windowed delivery of snapshot chunks. Progress, not elapsed time, decides liveness:
/// any increase of the acknowledged position resets the stall clock, so a slow link keeps going while a dead one is detected.
/// </summary>
public sealed class ChunkWindow
{
    private readonly int window;
    private readonly long baseRto, maxRto, stallTicks;
    private readonly long[] sentAt;
    private readonly bool[] resent;
    private long lastProgress, lastSend;
    private long rto;
    private long srtt = -1, rttvar;
    private int highWater;

    /// <param name="baseRtoTicks">Floor of the retransmission timeout; the real timeout follows the measured round trip (smoothed rtt + 4 deviations) between this and <paramref name="maxRtoTicks"/>.</param>
    public ChunkWindow(int window, long baseRtoTicks, long maxRtoTicks, long stallTicks, long now)
    {
        this.window = window; baseRto = rto = baseRtoTicks; maxRto = maxRtoTicks; this.stallTicks = stallTicks; lastProgress = lastSend = now;
        sentAt = new long[window]; resent = new bool[window];
    }

    /// <summary>Smoothed round trip of a chunk and its acknowledgement; -1 until the first measurement.</summary>
    public long SmoothedRtt => srtt;

    /// <summary>Cumulative count of chunks the client confirmed.</summary>
    public int Acked { get; private set; }
    /// <summary>Index of the next chunk to transmit.</summary>
    public int NextToSend { get; private set; }
    public int Produced { get; private set; }
    public bool Stalled { get; private set; }
    public int Retransmissions { get; private set; }

    public void OnProduced(int totalProduced) { Produced = totalProduced; }

    public bool CanSend => NextToSend < Produced && NextToSend - Acked < window;

    public int Take(long now)
    {
        int idx = NextToSend++;
        int slot = idx % window;
        resent[slot] = idx < highWater;
        sentAt[slot] = now;
        if (NextToSend > highWater) { highWater = NextToSend; }
        if (idx == Acked) { lastSend = now; }
        return idx;
    }

    public void OnAck(int cumulative, long now)
    {
        if (cumulative <= Acked || cumulative > highWater) { return; }
        // Only a chunk sent exactly once gives an unambiguous round trip (the ack could belong to either copy of a resent one).
        int slot = (cumulative - 1) % window;
        if (!resent[slot] && cumulative - 1 >= Acked) { Measure(now - sentAt[slot]); }
        Acked = cumulative; lastProgress = now; lastSend = now;
        // Without a measurement the backed-off timeout stays: dropping to the base again would resend the window before its first ack can arrive, forever.
        if (srtt >= 0) { rto = Math.Clamp(srtt + 4 * rttvar, baseRto, maxRto); }
        if (NextToSend < Acked) { NextToSend = Acked; }
    }

    private void Measure(long sample)
    {
        if (sample < 0) { return; }
        if (srtt < 0) { srtt = sample; rttvar = sample / 2; return; }
        rttvar = (3 * rttvar + Math.Abs(srtt - sample)) / 4;
        srtt = (7 * srtt + sample) / 8;
    }

    /// <summary>Go-back-N on a quiet link; flags Stalled only after stallTicks without any ack progress while data is outstanding.</summary>
    public void Poll(long now)
    {
        if (Acked == highWater) { lastProgress = now; return; }
        if (now - lastProgress >= stallTicks) { Stalled = true; return; }
        if (now - lastSend >= rto)
        {
            NextToSend = Acked; lastSend = now; Retransmissions++;
            rto = Math.Min(rto * 2, maxRto);
        }
    }
}
