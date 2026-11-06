using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;

namespace INet;

public static class NetSequence
{
    static long epoch = DateTime.UtcNow.Ticks;
    public static ulong NextEpoch() => (ulong)Interlocked.Increment(ref epoch);
    public static bool IsNewer(uint incoming, uint current) => unchecked((int)(incoming - current)) > 0;
}

public sealed class ByteStateAuthority
{
    public const int CommonHeaderSize = 13;
    public const int SnapshotHeaderSize = 19;
    public const int MaxFrameSize = SnapshotHeaderSize + ByteDeltaCodec.PageSize * 3;
    internal const byte SnapshotKind = 0x10, DeltaKind = 0x11;
    ushort[] entities = Array.Empty<ushort>();
    byte[] values = Array.Empty<byte>();
    ulong[] dirty = Array.Empty<ulong>();
    public int Count => values.Length;
    public int Pages => (Count + ByteDeltaCodec.PageSize - 1) / ByteDeltaCodec.PageSize;
    public ulong Epoch { get; private set; }
    public uint Revision { get; private set; }

    public void Configure(ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> initial)
    {
        if (ids.Length != initial.Length || ids.Length > ushort.MaxValue) { throw new ArgumentException("Invalid state schema"); }
        var unique = new HashSet<ushort>(ids.Length);
        foreach (ushort id in ids) { if (id == 0 || !unique.Add(id)) { throw new ArgumentException("Entity ids must be nonzero and unique"); } }
        entities = ids.ToArray(); values = initial.ToArray(); dirty = new ulong[(Count + 63) / 64];
        Epoch = NetSequence.NextEpoch(); Revision = 1;
        MarkAllDirty();
    }

    public void Set(int slot, byte value)
    {
        if (values[slot] == value) { return; }
        values[slot] = value;
        dirty[slot >> 6] |= 1UL << (slot & 63);
    }

    public void Advance() => Revision = unchecked(Revision + 1);
    public void MarkAllDirty()
    {
        dirty.AsSpan().Fill(ulong.MaxValue);
        if ((Count & 63) != 0) { dirty[^1] = (1UL << (Count & 63)) - 1; }
    }

    public void MarkSent(int page)
    {
        if ((uint)page >= Pages) { throw new ArgumentOutOfRangeException(nameof(page)); }
        int start = page * 4;
        dirty.AsSpan(start, Math.Min(4, dirty.Length - start)).Clear();
    }

    public int WriteDelta(Span<byte> output, int page, bool compact = true)
        => WriteDelta(output, page, out _, compact);

    public int WriteDelta(Span<byte> output, int page, out int entries, bool compact = true)
    {
        if ((uint)page >= Pages) { throw new ArgumentOutOfRangeException(nameof(page)); }
        int start = page * ByteDeltaCodec.PageSize, count = Math.Min(ByteDeltaCodec.PageSize, Count - start);
        if (output.Length < CommonHeaderSize) { throw new ArgumentException("State output is too small"); }
        int length = ByteDeltaCodec.EncodePage(output.Slice(CommonHeaderSize), start, values.AsSpan(start, count), dirty.AsSpan(page * 4, (count + 63) / 64), out entries, compact);
        if (length == 0) { return 0; }
        WriteHeader(output, DeltaKind);
        return CommonHeaderSize + length;
    }

    public int WriteSnapshot(Span<byte> output, int start, out int written)
    {
        if ((uint)start > Count || start == Count && Count != 0) { throw new ArgumentOutOfRangeException(nameof(start)); }
        written = Math.Min(ByteDeltaCodec.PageSize, Count - start);
        int length = SnapshotHeaderSize + written * 3;
        if (output.Length < length) { throw new ArgumentException("Snapshot output is too small"); }
        WriteHeader(output, SnapshotKind);
        BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(13), (ushort)Count);
        BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(15), (ushort)start);
        BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(17), (ushort)written);
        for (int i = 0; i < written; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(SnapshotHeaderSize + i * 3), entities[start + i]);
            output[SnapshotHeaderSize + i * 3 + 2] = values[start + i];
        }
        return length;
    }

    void WriteHeader(Span<byte> output, byte kind)
    {
        if (Epoch == 0) { throw new InvalidOperationException("State schema has not been configured"); }
        output[0] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(output.Slice(1), Epoch);
        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(9), Revision);
    }
}

public sealed class ByteStateReplica
{
    ushort[] entities = Array.Empty<ushort>();
    byte[] values = Array.Empty<byte>();
    uint[] revisions = Array.Empty<uint>();
    bool[] known = Array.Empty<bool>(), versioned = Array.Empty<bool>();
    readonly Dictionary<ushort, int> slots = new();
    int mapped;
    bool needsSnapshot;
    public ulong Epoch { get; private set; }
    public int Count => values.Length;
    public bool NeedsSnapshot => needsSnapshot;
    public bool Synced => Epoch != 0 && !NeedsSnapshot && mapped == Count;
    public ushort EntityAt(int slot) => entities[slot];
    public byte ValueAt(int slot) => values[slot];

    public void Reset()
    {
        Epoch = 0; needsSnapshot = false; mapped = 0; slots.Clear();
        entities = Array.Empty<ushort>(); values = Array.Empty<byte>(); revisions = Array.Empty<uint>();
        known = versioned = Array.Empty<bool>();
    }

    public bool TryApply(ReadOnlySpan<byte> frame, Span<ByteStateChange> applied, out int count)
    {
        count = 0;
        if (frame.Length < ByteStateAuthority.CommonHeaderSize || frame.Length > ByteStateAuthority.MaxFrameSize) { return false; }
        ulong epoch = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(1));
        uint revision = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(9));
        if (epoch == 0) { return false; }
        if (frame[0] == ByteStateAuthority.SnapshotKind) { return Snapshot(frame, epoch, revision, applied, out count); }
        if (frame[0] != ByteStateAuthority.DeltaKind) { return false; }
        Span<ByteStateChange> changes = stackalloc ByteStateChange[ByteDeltaCodec.PageSize];
        if (!ByteDeltaCodec.TryDecode(frame.Slice(ByteStateAuthority.CommonHeaderSize), changes, out int n, out int start, out int range) || applied.Length < n) { return false; }
        if (epoch != Epoch) { if (epoch > Epoch) { needsSnapshot = true; } return true; }
        if (start + range > Count) { return false; }
        for (int i = 0; i < n; i++)
        {
            ByteStateChange change = changes[i];
            if (!Store(change.Slot, change.Value, revision) || !known[change.Slot]) { continue; }
            applied[count++] = change;
        }
        return true;
    }

    bool Snapshot(ReadOnlySpan<byte> frame, ulong epoch, uint revision, Span<ByteStateChange> applied, out int count)
    {
        count = 0;
        if (frame.Length < ByteStateAuthority.SnapshotHeaderSize) { return false; }
        int total = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(13));
        int start = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(15));
        int n = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(17));
        if (n > ByteDeltaCodec.PageSize || start + n > total || total > 0 && n == 0 || applied.Length < n || frame.Length != ByteStateAuthority.SnapshotHeaderSize + n * 3) { return false; }
        if (epoch < Epoch) { return true; }
        bool fresh = epoch != Epoch;
        if (!fresh && total != Count) { return false; }
        Span<ushort> sorted = stackalloc ushort[ByteDeltaCodec.PageSize];
        for (int i = 0; i < n; i++)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(ByteStateAuthority.SnapshotHeaderSize + i * 3));
            if (id == 0 || !fresh && (known[start + i] && entities[start + i] != id || slots.TryGetValue(id, out int previous) && previous != start + i)) { return false; }
            sorted[i] = id;
        }
        sorted.Slice(0, n).Sort();
        for (int i = 1; i < n; i++) { if (sorted[i] == sorted[i - 1]) { return false; } }
        if (fresh)
        {
            entities = new ushort[total]; values = new byte[total]; revisions = new uint[total];
            known = new bool[total]; versioned = new bool[total];
            slots.Clear(); slots.EnsureCapacity(total); mapped = 0; Epoch = epoch;
        }
        for (int i = 0; i < n; i++)
        {
            int slot = start + i, offset = ByteStateAuthority.SnapshotHeaderSize + i * 3;
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset));
            if (!known[slot]) { entities[slot] = id; slots.Add(id, slot); known[slot] = true; mapped++; }
            Store(slot, frame[offset + 2], revision);
            applied[count++] = new ByteStateChange((ushort)slot, values[slot]);
        }
        needsSnapshot = false;
        return true;
    }

    bool Store(int slot, byte value, uint revision)
    {
        if (versioned[slot] && !NetSequence.IsNewer(revision, revisions[slot])) { return false; }
        values[slot] = value; revisions[slot] = revision; versioned[slot] = true;
        return true;
    }
}
