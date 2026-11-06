using System;

namespace PrimaryPackage.SubEditor;

/// <summary>
/// Bounded replay window of encoded frames with contiguous revisions. Append is allocation-free.
/// Anything older than the window is gone by design: a client further behind gets a snapshot, never an unbounded log.
/// </summary>
public sealed class OpRing
{
    private readonly byte[] buffer;
    private readonly int[] offsets, lengths;
    private readonly ulong[] revs;
    private int head, count, writePos;

    public OpRing(int byteCapacity, int maxFrames)
    {
        buffer = new byte[byteCapacity]; offsets = new int[maxFrames]; lengths = new int[maxFrames]; revs = new ulong[maxFrames];
    }

    public int Count => count;
    public ulong OldestRev => count == 0 ? 0 : revs[head];
    public ulong NewestRev => count == 0 ? 0 : revs[(head + count - 1) % revs.Length];

    public bool CanAppend(ulong rev, int bytes) => bytes >= 0 && bytes <= buffer.Length && (count == 0 || rev == NewestRev + 1);

    public bool Append(ulong rev, ReadOnlySpan<byte> frame)
    {
        if (!CanAppend(rev, frame.Length)) { return false; }
        if (writePos + frame.Length > buffer.Length)
        {
            // Wrapping skips the tail, so older frames still parked there would stop being the head of the FIFO.
            // Drop them first, otherwise the overlap test below could miss a newer frame that gets overwritten.
            while (count > 0 && offsets[head] >= writePos) { head = (head + 1) % revs.Length; count--; }
            writePos = 0;
        }
        int end = writePos + frame.Length;
        while (count > 0 && (count == revs.Length || (offsets[head] < end && offsets[head] + lengths[head] > writePos)))
        { head = (head + 1) % revs.Length; count--; }
        int slot = (head + count) % revs.Length;
        frame.CopyTo(buffer.AsSpan(writePos));
        offsets[slot] = writePos; lengths[slot] = frame.Length; revs[slot] = rev;
        count++; writePos = end;
        return true;
    }

    /// <summary>Copy-free lookup; the span is valid until the next Append.</summary>
    public bool TryGet(ulong rev, out ReadOnlySpan<byte> frame)
    {
        frame = default;
        if (count == 0 || rev < OldestRev || rev > NewestRev) { return false; }
        int slot = (head + (int)(rev - OldestRev)) % revs.Length;
        frame = buffer.AsSpan(offsets[slot], lengths[slot]);
        return true;
    }
}
