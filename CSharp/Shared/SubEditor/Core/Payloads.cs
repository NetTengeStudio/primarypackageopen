using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PrimaryPackage.SubEditor;

/// <summary>Growable pooled byte sink. Dispose returns the buffer.</summary>
public struct ByteWriter : IDisposable
{
    private byte[] buf;
    private int pos;

    public ByteWriter(int capacity) { buf = ArrayPool<byte>.Shared.Rent(Math.Max(64, capacity)); pos = 0; }

    public readonly int Length => pos;
    public readonly ReadOnlySpan<byte> Written => buf.AsSpan(0, pos);

    private void Ensure(int n)
    {
        if (pos + n <= buf.Length) { return; }
        byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Max(buf.Length * 2, pos + n));
        buf.AsSpan(0, pos).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(buf);
        buf = bigger;
    }

    public void U8(byte v) { Ensure(1); buf[pos++] = v; }
    public void F32(float v) { Ensure(4); BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos), v); pos += 4; }
    public void V(ulong v) { Ensure(10); VarInt.Write(buf, ref pos, v); }
    public void Z(long v) => V((ulong)((v << 1) ^ (v >> 63)));
    public void Bytes(ReadOnlySpan<byte> data) { V((ulong)data.Length); Ensure(data.Length); data.CopyTo(buf.AsSpan(pos)); pos += data.Length; }
    public void Str(string s)
    {
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        byte[] tmp = ArrayPool<byte>.Shared.Rent(max);
        try { int n = Encoding.UTF8.GetBytes(s, 0, s.Length, tmp, 0); Bytes(tmp.AsSpan(0, n)); }
        finally { ArrayPool<byte>.Shared.Return(tmp); }
    }

    public void Dispose()
    {
        if (buf != null) { ArrayPool<byte>.Shared.Return(buf); buf = null; }
    }
}

/// <summary>Bounds-checked reader; any short or malformed read sets Failed instead of throwing.</summary>
public ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> data;
    private int pos;
    public bool Failed { get; private set; }

    public ByteReader(ReadOnlySpan<byte> data) { this.data = data; pos = 0; Failed = false; }
    public readonly bool AtEnd => pos == data.Length;
    public readonly int Remaining => data.Length - pos;

    public byte U8() { if (pos >= data.Length) { Failed = true; return 0; } return data[pos++]; }
    public float F32()
    {
        if (data.Length - pos < 4) { Failed = true; pos = data.Length; return 0; }
        float v = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(pos)); pos += 4; return v;
    }
    public ulong V() { if (!VarInt.TryRead(data, ref pos, out ulong v)) { Failed = true; pos = data.Length; return 0; } return v; }
    public long Z() { ulong u = V(); return (long)(u >> 1) ^ -(long)(u & 1); }
    public ReadOnlySpan<byte> Bytes(int max)
    {
        ulong n = V();
        if (Failed || n > (ulong)max || n > (ulong)(data.Length - pos)) { Failed = true; return default; }
        var s = data.Slice(pos, (int)n); pos += (int)n; return s;
    }
    public string Str(int maxBytes = 4096)
    {
        var b = Bytes(maxBytes);
        return Failed ? "" : Encoding.UTF8.GetString(b);
    }
}

[Flags]
public enum TransformFields : byte { None = 0, Position = 1, Size = 2, Rotation = 4, Scale = 8, Flags = 16, SpriteDepth = 32, Frame = 64 }

/// <summary>One entity's transform delta. Only the fields named in Fields are present on the wire.</summary>
public struct TransformRecord
{
    public TransformFields Fields;
    public float X, Y;
    public int Width, Height;
    public float Rotation, Scale, SpriteDepth;
    public byte Flags;
    public ushort SubmarineId;
}

/// <summary>
/// Transform payload: one record per touched id, in the header's ascending id order, so ids are never repeated here.
/// Moving a selection of N walls costs 1 + 8 bytes each instead of a full record.
/// </summary>
public static class TransformPayload
{
    public static void Write(ref ByteWriter w, ReadOnlySpan<TransformRecord> records)
    {
        w.V((ulong)records.Length);
        for (int i = 0; i < records.Length; i++)
        {
            ref readonly TransformRecord r = ref records[i];
            w.U8((byte)r.Fields);
            if ((r.Fields & TransformFields.Position) != 0) { w.F32(r.X); w.F32(r.Y); }
            if ((r.Fields & TransformFields.Size) != 0) { w.Z(r.Width); w.Z(r.Height); }
            if ((r.Fields & TransformFields.Rotation) != 0) { w.F32(r.Rotation); }
            if ((r.Fields & TransformFields.Scale) != 0) { w.F32(r.Scale); }
            if ((r.Fields & TransformFields.Flags) != 0) { w.U8(r.Flags); }
            if ((r.Fields & TransformFields.SpriteDepth) != 0) { w.F32(r.SpriteDepth); }
            if ((r.Fields & TransformFields.Frame) != 0) { w.V(r.SubmarineId); }
        }
    }

    /// <summary>Reads exactly expectedCount records (the header's touched count) or fails.</summary>
    public static bool TryRead(ReadOnlySpan<byte> payload, int expectedCount, Span<TransformRecord> dest)
    {
        var rd = new ByteReader(payload);
        if (rd.V() != (ulong)expectedCount || dest.Length < expectedCount) { return false; }
        for (int i = 0; i < expectedCount; i++)
        {
            var r = new TransformRecord { Fields = (TransformFields)rd.U8() };
            if (((byte)r.Fields & ~127) != 0) { return false; }
            if ((r.Fields & TransformFields.Position) != 0) { r.X = rd.F32(); r.Y = rd.F32(); }
            if ((r.Fields & TransformFields.Size) != 0)
            {
                long width = rd.Z(), height = rd.Z();
                if (width < 0 || width > int.MaxValue || height < 0 || height > int.MaxValue) { return false; }
                r.Width = (int)width; r.Height = (int)height;
            }
            if ((r.Fields & TransformFields.Rotation) != 0) { r.Rotation = rd.F32(); }
            if ((r.Fields & TransformFields.Scale) != 0) { r.Scale = rd.F32(); }
            if ((r.Fields & TransformFields.Flags) != 0) { r.Flags = rd.U8(); }
            if ((r.Fields & TransformFields.SpriteDepth) != 0) { r.SpriteDepth = rd.F32(); }
            if ((r.Fields & TransformFields.Frame) != 0)
            {
                ulong submarine = rd.V();
                if (submarine > ushort.MaxValue) { return false; }
                r.SubmarineId = (ushort)submarine;
            }
            if (rd.Failed || !float.IsFinite(r.X) || !float.IsFinite(r.Y) || !float.IsFinite(r.Rotation) || !float.IsFinite(r.Scale) || !float.IsFinite(r.SpriteDepth) ||
                (r.Fields & TransformFields.Position) != 0 && (r.X < int.MinValue || r.X >= (float)int.MaxValue || r.Y < int.MinValue || r.Y >= (float)int.MaxValue) ||
                (r.Fields & TransformFields.Scale) != 0 && r.Scale <= 0 || (r.Flags & ~3) != 0) { return false; }
            dest[i] = r;
        }
        return rd.AtEnd;
    }
}

/// <summary>
/// Property payload: the property name is sent once; each touched entity (header order) carries its component index
/// (-1 for the entity itself) and, unless all values are equal, its own value. Multi-selection edits therefore cost
/// a few bytes per entity, and the server-side inverse (old values) uses the same shape with per-entity values.
/// </summary>
public static class PropertyPayload
{
    public const int MaxValueBytes = 2048;

    public static void Write(ref ByteWriter w, string property, ReadOnlySpan<short> componentIndex, ReadOnlySpan<string> values)
    {
        if (componentIndex.Length != values.Length) { throw new ArgumentException("length mismatch"); }
        bool same = true;
        for (int i = 1; i < values.Length && same; i++) { same = string.Equals(values[i], values[0], StringComparison.Ordinal); }
        w.Str(property);
        w.U8(same ? (byte)1 : (byte)0);
        w.V((ulong)values.Length);
        if (same && values.Length > 0) { w.Str(values[0]); }
        for (int i = 0; i < values.Length; i++)
        {
            w.Z(componentIndex[i]);
            if (!same) { w.Str(values[i]); }
        }
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, int expectedCount, out string property, short[] componentIndex, string[] values)
    {
        property = "";
        var rd = new ByteReader(payload);
        property = rd.Str(256);
        bool same = rd.U8() == 1;
        if (rd.V() != (ulong)expectedCount || componentIndex.Length < expectedCount || values.Length < expectedCount) { return false; }
        string shared = same && expectedCount > 0 ? rd.Str(MaxValueBytes) : null;
        for (int i = 0; i < expectedCount; i++)
        {
            long c = rd.Z();
            if (c < -1 || c > short.MaxValue) { return false; }
            componentIndex[i] = (short)c;
            values[i] = same ? shared : rd.Str(MaxValueBytes);
            if (rd.Failed) { return false; }
        }
        return !rd.Failed && rd.AtEnd;
    }
}

/// <summary>Opaque serialized entity XML for Spawn and for the undo of Delete. Brotli above a small threshold.</summary>
public static class BlobPayload
{
    public const int CompressAbove = 256;
    public const int MaxUncompressed = 8 << 20;

    public static void Write(ref ByteWriter w, ReadOnlySpan<byte> utf8Xml)
    {
        if (utf8Xml.Length <= CompressAbove) { w.U8(0); w.Bytes(utf8Xml); return; }
        using var ms = new MemoryStream(utf8Xml.Length / 3 + 32);
        using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true)) { br.Write(utf8Xml); }
        w.U8(1); w.V((ulong)utf8Xml.Length); w.Bytes(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out byte[] utf8Xml)
    {
        utf8Xml = null;
        var rd = new ByteReader(payload);
        byte mode = rd.U8();
        if (mode == 0) { var b = rd.Bytes(CompressAbove); if (rd.Failed || !rd.AtEnd) { return false; } utf8Xml = b.ToArray(); return true; }
        if (mode != 1) { return false; }
        ulong raw = rd.V();
        var comp = rd.Bytes(MaxUncompressed);
        if (rd.Failed || !rd.AtEnd || raw > MaxUncompressed || raw <= CompressAbove) { return false; }
        try
        {
            byte[] outBuf = new byte[raw];
            using var input = new MemoryStream(comp.ToArray(), writable: false);
            using var br = new BrotliStream(input, CompressionMode.Decompress);
            int total = 0, n;
            while (total < outBuf.Length && (n = br.Read(outBuf, total, outBuf.Length - total)) > 0) { total += n; }
            if (total != outBuf.Length || br.ReadByte() != -1) { return false; }
            utf8Xml = outBuf; return true;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException) { return false; }
    }
}

/// <summary>Link payload: one (linked) flag per pair; touched ids in the header are the pair members, ascending.</summary>
public static class LinkPayload
{
    public static void Write(ref ByteWriter w, ReadOnlySpan<(ushort A, ushort B, bool Linked)> pairs)
    {
        w.V((ulong)pairs.Length);
        for (int i = 0; i < pairs.Length; i++) { w.V(pairs[i].A); w.V(pairs[i].B); w.U8(pairs[i].Linked ? (byte)1 : (byte)0); }
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, int maxPairs, out (ushort A, ushort B, bool Linked)[] pairs)
    {
        pairs = null;
        var rd = new ByteReader(payload);
        ulong n = rd.V();
        if (rd.Failed || n > (ulong)maxPairs || n * 3 > (ulong)rd.Remaining) { return false; }
        var result = new (ushort, ushort, bool)[n];
        for (int i = 0; i < (int)n; i++)
        {
            ulong a = rd.V(), b = rd.V(); byte l = rd.U8();
            if (rd.Failed || a == 0 || b == 0 || a > ushort.MaxValue || b > ushort.MaxValue || l > 1) { return false; }
            result[i] = ((ushort)a, (ushort)b, l == 1);
        }
        if (!rd.AtEnd) { return false; }
        pairs = result; return true;
    }
}

/// <summary>Spawn payload: target submarine id (0 = main sub) followed by the entity XML blob.</summary>
public static class SpawnPayload
{
    public static void Write(ref ByteWriter w, ushort submarineId, ReadOnlySpan<byte> utf8Xml)
    {
        w.V(submarineId);
        BlobPayload.Write(ref w, utf8Xml);
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ushort submarineId, out byte[] utf8Xml)
    {
        submarineId = 0; utf8Xml = null;
        var rd = new ByteReader(payload);
        ulong sub = rd.V();
        if (rd.Failed || sub > ushort.MaxValue) { return false; }
        submarineId = (ushort)sub;
        int consumed = payload.Length - rd.Remaining;
        return BlobPayload.TryRead(payload.Slice(consumed), out utf8Xml);
    }
}

/// <summary>State payload: one opaque per-entity state blob (header order). Used for tool edits whose result is easier to describe as "the entity now looks like this".</summary>
public static class StatePayload
{
    public const int MaxBlob = 16 * 1024;

    public static void Write(ref ByteWriter w, ReadOnlySpan<byte[]> blobs)
    {
        w.V((ulong)blobs.Length);
        for (int i = 0; i < blobs.Length; i++) { w.Bytes(blobs[i]); }
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, int expectedCount, out byte[][] blobs)
    {
        blobs = null;
        var rd = new ByteReader(payload);
        if (rd.V() != (ulong)expectedCount) { return false; }
        var result = new byte[expectedCount][];
        for (int i = 0; i < expectedCount; i++) { result[i] = rd.Bytes(MaxBlob).ToArray(); if (rd.Failed) { return false; } }
        if (!rd.AtEnd) { return false; }
        blobs = result; return true;
    }
}

/// <summary>Where an item sits: container item id (0 = not in a container), index of the ItemContainer component, slot.</summary>
public readonly record struct InventoryPlacement(ushort ContainerId, short ComponentIndex, int Slot,
    ushort SubmarineId = ushort.MaxValue, int X = 0, int Y = 0, float Rotation = 0);

/// <summary>Inventory payload: one placement per touched item (header order).</summary>
public static class InventoryPayload
{
    public static void Write(ref ByteWriter w, ReadOnlySpan<InventoryPlacement> placements)
    {
        w.V((ulong)placements.Length);
        foreach (ref readonly InventoryPlacement p in placements)
        {
            w.V(p.ContainerId); w.Z(p.ComponentIndex); w.Z(p.Slot);
            if (p.ContainerId == 0) { w.V(p.SubmarineId); w.Z(p.X); w.Z(p.Y); w.F32(p.Rotation); }
        }
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, int expectedCount, out InventoryPlacement[] placements)
    {
        placements = null;
        var rd = new ByteReader(payload);
        if (expectedCount < 0 || expectedCount > OpFrame.MaxTouched || rd.V() != (ulong)expectedCount) { return false; }
        var result = new InventoryPlacement[expectedCount];
        for (int i = 0; i < expectedCount; i++)
        {
            ulong c = rd.V(); long comp = rd.Z(), slot = rd.Z();
            if (rd.Failed || c > ushort.MaxValue || comp < -1 || comp > short.MaxValue || slot < -1 || slot > 4096) { return false; }
            if (c == 0)
            {
                ulong sub = rd.V(); long x = rd.Z(), y = rd.Z(); float rotation = rd.F32();
                if (rd.Failed || comp != -1 || slot != -1 || sub > ushort.MaxValue || x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue || !float.IsFinite(rotation)) { return false; }
                result[i] = new InventoryPlacement(0, -1, -1, (ushort)sub, (int)x, (int)y, rotation);
            }
            else
            {
                if (comp < 0 || slot < 0) { return false; }
                result[i] = new InventoryPlacement((ushort)c, (short)comp, (int)slot);
            }
        }
        if (!rd.AtEnd) { return false; }
        placements = result; return true;
    }
}

/// <summary>One wire end: connected item id (0 = free) and the index of the connection in that item's connection panel.</summary>
public readonly record struct WireEnd(ushort ItemId, short ConnectionIndex);

public readonly record struct WireState(ushort WireId, WireEnd End0, WireEnd End1, (float X, float Y)[] Nodes);

/// <summary>Wire payload: self-describing list of wires. The server derives the touched set (wires plus every item at an old or new end).</summary>
public static class WirePayload
{
    public const int MaxWires = 256, MaxNodes = 512;

    public static void Write(ref ByteWriter w, ReadOnlySpan<WireState> wires)
    {
        w.V((ulong)wires.Length);
        for (int i = 0; i < wires.Length; i++)
        {
            ref readonly WireState s = ref wires[i];
            w.V(s.WireId);
            WriteEnd(ref w, s.End0); WriteEnd(ref w, s.End1);
            w.V((ulong)s.Nodes.Length);
            foreach (var (x, y) in s.Nodes) { w.F32(x); w.F32(y); }
        }
    }

    private static void WriteEnd(ref ByteWriter w, WireEnd e) { w.V(e.ItemId); if (e.ItemId != 0) { w.Z(e.ConnectionIndex); } }

    private static bool ReadEnd(ref ByteReader rd, out WireEnd end)
    {
        end = default;
        ulong id = rd.V();
        if (rd.Failed || id > ushort.MaxValue) { return false; }
        if (id == 0) { return true; }
        long idx = rd.Z();
        if (rd.Failed || idx < -1 || idx > short.MaxValue) { return false; }
        end = new WireEnd((ushort)id, (short)idx);
        return true;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out WireState[] wires)
    {
        wires = null;
        var rd = new ByteReader(payload);
        ulong n = rd.V();
        if (rd.Failed || n == 0 || n > MaxWires) { return false; }
        var result = new WireState[n];
        for (int i = 0; i < (int)n; i++)
        {
            ulong id = rd.V();
            if (!ReadEnd(ref rd, out WireEnd e0) || !ReadEnd(ref rd, out WireEnd e1)) { return false; }
            ulong nodeCount = rd.V();
            if (rd.Failed || id == 0 || id > ushort.MaxValue || i > 0 && id <= result[i - 1].WireId || e0.ItemId != 0 && e0 == e1 || nodeCount > MaxNodes || nodeCount * 8 > (ulong)rd.Remaining) { return false; }
            var nodes = new (float, float)[nodeCount];
            for (int k = 0; k < nodes.Length; k++)
            {
                float x = rd.F32(), y = rd.F32();
                if (!float.IsFinite(x) || !float.IsFinite(y)) { return false; }
                nodes[k] = (x, y);
            }
            if (rd.Failed) { return false; }
            result[i] = new WireState((ushort)id, e0, e1, nodes);
        }
        if (!rd.AtEnd) { return false; }
        wires = result; return true;
    }
}

/// <summary>
/// Batch payload: a sequence of complete sub-operations applied in order and undone as one unit
/// (auto-hull, waypoint generation, tool edits that touch walls and wires together).
/// </summary>
public static class BatchPayload
{
    public const int MaxOps = 512, MaxTotalIds = 4096;

    public readonly record struct Sub(OpKind Kind, ushort[] Ids, byte[] Payload);

    public static void Write(ref ByteWriter w, ReadOnlySpan<Sub> subs)
    {
        w.V((ulong)subs.Length);
        for (int i = 0; i < subs.Length; i++)
        {
            w.U8((byte)subs[i].Kind);
            w.V((ulong)subs[i].Ids.Length);
            ushort prev = 0;
            for (int k = 0; k < subs[i].Ids.Length; k++) { w.V(k == 0 ? subs[i].Ids[0] : (ulong)(subs[i].Ids[k] - prev)); prev = subs[i].Ids[k]; }
            w.Bytes(subs[i].Payload);
        }
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, int maxPayload, out Sub[] subs)
    {
        subs = null;
        var rd = new ByteReader(payload);
        ulong n = rd.V();
        if (rd.Failed || n == 0 || n > MaxOps) { return false; }
        var result = new Sub[n]; int totalIds = 0;
        for (int i = 0; i < (int)n; i++)
        {
            byte kind = rd.U8(); ulong count = rd.V();
            if (rd.Failed || kind == (byte)OpKind.Batch || kind == 0 || count > MaxTotalIds || (totalIds += (int)count) > MaxTotalIds) { return false; }
            var ids = new ushort[count]; uint prev = 0;
            for (int k = 0; k < ids.Length; k++)
            {
                ulong d = rd.V();
                if (rd.Failed || d > ushort.MaxValue || (k > 0 && d == 0)) { return false; }
                prev = k == 0 ? (uint)d : prev + (uint)d;
                if (prev > ushort.MaxValue) { return false; }
                ids[k] = (ushort)prev;
            }
            result[i] = new Sub((OpKind)kind, ids, rd.Bytes(maxPayload).ToArray());
            if (rd.Failed) { return false; }
        }
        if (!rd.AtEnd) { return false; }
        subs = result; return true;
    }
}

/// <summary>
/// Submarine metadata payload: named string fields, ascending by key. Only changed fields travel; the server answers with the
/// previous values of the same keys, so undo is the same shape. Keys are fixed by the engine adapter, values are opaque here.
/// </summary>
public static class MetaPayload
{
    public const int MaxPairs = 32, MaxKeyBytes = 48, MaxValueBytes = 4096;

    public readonly record struct Pair(string Key, string Value);

    public static void Write(ref ByteWriter w, ReadOnlySpan<Pair> pairs)
    {
        w.V((ulong)pairs.Length);
        for (int i = 0; i < pairs.Length; i++) { w.Str(pairs[i].Key); w.Str(pairs[i].Value); }
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out Pair[] pairs)
    {
        pairs = null;
        var rd = new ByteReader(payload);
        ulong n = rd.V();
        if (rd.Failed || n == 0 || n > MaxPairs) { return false; }
        var result = new Pair[n];
        string previous = null;
        for (int i = 0; i < result.Length; i++)
        {
            string key = rd.Str(MaxKeyBytes), value = rd.Str(MaxValueBytes);
            if (rd.Failed || key.Length == 0 || (previous != null && string.CompareOrdinal(previous, key) >= 0)) { return false; }
            result[i] = new Pair(key, value); previous = key;
        }
        if (!rd.AtEnd) { return false; }
        pairs = result; return true;
    }
}

/// <summary>
/// A submarine loaded into the running round. Everything a peer needs to build the same submarine without a file transfer:
/// which saved submarine (name and content hash), the entity id window and the submarine's own id (the engine addresses
/// position updates by it), and where it appears.
/// </summary>
/// <param name="Thalamus">For a wreck: whether it keeps its Thalamus AI (otherwise the wreck is inert). Ignored for other submarine types.</param>
public readonly record struct SubSpawn(string Name, string Hash, ushort SubmarineId, int IdOffset, float X, float Y, bool Thalamus = false, bool Exact = false);

public static class SubSpawnPayload
{
    public const int MaxName = 256, MaxHash = 128;

    public static void Write(ref ByteWriter w, in SubSpawn s)
    {
        w.Str(s.Name); w.Str(s.Hash); w.V(s.SubmarineId); w.Z(s.IdOffset); w.F32(s.X); w.F32(s.Y); w.U8((byte)((s.Thalamus ? 1 : 0) | (s.Exact ? 2 : 0)));
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out SubSpawn spawn)
    {
        spawn = default;
        var rd = new ByteReader(payload);
        string name = rd.Str(MaxName), hash = rd.Str(MaxHash);
        ulong sub = rd.V(); long offset = rd.Z(); float x = rd.F32(), y = rd.F32(); byte flags = rd.U8();
        if (rd.Failed || !rd.AtEnd || flags > 3 || name.Length == 0 || sub == 0 || sub > ushort.MaxValue || offset < 0 || offset >= ushort.MaxValue || !float.IsFinite(x) || !float.IsFinite(y)) { return false; }
        spawn = new SubSpawn(name, hash, (ushort)sub, (int)offset, x, y, (flags & 1) != 0, (flags & 2) != 0);
        return true;
    }
}

/// <summary>
/// A wire drawn in the editor: the item exists only on the drawing client until the server accepts it.
/// The wire's connections refer to the drawing client's id; the server replaces it with the id it assigns.
/// </summary>
public static class WireNewPayload
{
    public static void Write(ref ByteWriter w, ushort submarineId, ReadOnlySpan<byte> utf8Xml, in WireState state)
    {
        var spawn = new ByteWriter(utf8Xml.Length / 2 + 16);
        var wire = new ByteWriter(64);
        try
        {
            SpawnPayload.Write(ref spawn, submarineId, utf8Xml);
            WirePayload.Write(ref wire, new[] { state });
            w.Bytes(spawn.Written); w.Bytes(wire.Written);
        }
        finally { spawn.Dispose(); wire.Dispose(); }
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, int maxPart, out ushort submarineId, out byte[] utf8Xml, out WireState state)
    {
        submarineId = 0; utf8Xml = null; state = default;
        var rd = new ByteReader(payload);
        ReadOnlySpan<byte> spawn = rd.Bytes(maxPart), wire = rd.Bytes(maxPart);
        if (rd.Failed || !rd.AtEnd) { return false; }
        if (!SpawnPayload.TryRead(spawn, out submarineId, out utf8Xml) || !WirePayload.TryRead(wire, out WireState[] wires) || wires.Length != 1) { return false; }
        state = wires[0];
        return true;
    }
}

/// <summary>
/// One circuit box edit: which component of the box item it addresses and the engine's own serialized event
/// (opcode byte, then the event struct). A request carries the client's event, the broadcast carries the server's resolved one
/// (ids assigned, backing items named), so every peer feeds the engine's own handler the same bytes.
/// </summary>
public static class CircuitPayload
{
    public const int MaxEvent = 48 * 1024;

    public static void Write(ref ByteWriter w, byte componentIndex, ReadOnlySpan<byte> engineEvent)
    {
        w.U8(componentIndex); w.Bytes(engineEvent);
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out byte componentIndex, out byte[] engineEvent)
    {
        engineEvent = null;
        var rd = new ByteReader(payload);
        componentIndex = rd.U8();
        ReadOnlySpan<byte> body = rd.Bytes(MaxEvent);
        if (rd.Failed || !rd.AtEnd || body.Length < 1) { return false; }
        engineEvent = body.ToArray();
        return true;
    }
}

/// <summary>The submarines spawned so far, in spawn order, sent to a joiner before any entity chunk.</summary>
public static class SubListPayload
{
    public const int MaxSubs = 32;

    public static void Write(ref ByteWriter w, ReadOnlySpan<SubSpawn> subs)
    {
        w.V((ulong)subs.Length);
        for (int i = 0; i < subs.Length; i++)
        {
            var one = new ByteWriter(96);
            try { SubSpawnPayload.Write(ref one, subs[i]); w.Bytes(one.Written); }
            finally { one.Dispose(); }
        }
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out SubSpawn[] subs)
    {
        subs = null;
        var rd = new ByteReader(payload);
        ulong n = rd.V();
        if (rd.Failed || n > MaxSubs) { return false; }
        var result = new SubSpawn[n];
        for (int i = 0; i < result.Length; i++)
        {
            ReadOnlySpan<byte> one = rd.Bytes(1024);
            if (rd.Failed || !SubSpawnPayload.TryRead(one, out result[i])) { return false; }
        }
        if (!rd.AtEnd) { return false; }
        subs = result; return true;
    }
}

/// <summary>
/// Orientation of an entity after a mirror: which axes are flipped and where its rectangle ended up. Absolute values, so
/// applying the same record twice (the echo of an operation the client already did) changes nothing.
/// </summary>
public readonly record struct FlipRecord(bool FlippedX, bool FlippedY, int X, int Y, int Width, int Height, ushort SubmarineId = 0);

public static class FlipPayload
{
    public static void Write(ref ByteWriter w, ReadOnlySpan<FlipRecord> records)
    {
        w.V((ulong)records.Length);
        for (int i = 0; i < records.Length; i++)
        {
            ref readonly FlipRecord r = ref records[i];
            w.U8((byte)((r.FlippedX ? 1 : 0) | (r.FlippedY ? 2 : 0)));
            w.Z(r.X); w.Z(r.Y); w.Z(r.Width); w.Z(r.Height);
            w.V(r.SubmarineId);
        }
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, int expectedCount, FlipRecord[] dest)
    {
        var rd = new ByteReader(payload);
        if (rd.V() != (ulong)expectedCount || dest.Length < expectedCount) { return false; }
        for (int i = 0; i < expectedCount; i++)
        {
            byte flags = rd.U8();
            long x = rd.Z(), y = rd.Z(), width = rd.Z(), height = rd.Z();
            ulong submarine = rd.V();
            if (rd.Failed || flags > 3 || submarine > ushort.MaxValue || x != (int)x || y != (int)y || width != (int)width || height != (int)height || width < 0 || height < 0) { return false; }
            dest[i] = new FlipRecord((flags & 1) != 0, (flags & 2) != 0, (int)x, (int)y, (int)width, (int)height, (ushort)submarine);
        }
        return rd.AtEnd;
    }
}
