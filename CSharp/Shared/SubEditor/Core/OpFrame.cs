using System;
using System.Runtime.CompilerServices;

namespace PrimaryPackage.SubEditor;

/// <summary>Kind is opaque to the authority core; payload codecs per kind live next to the engine glue.</summary>
public enum OpKind : byte
{
    Spawn = 1, Delete = 2, Transform = 3, Property = 4, Link = 5, Wire = 6,
    Inventory = 8, SubMeta = 9, Waypoints = 11, Flip = 12, Batch = 14, SpawnSub = 15, State = 16, DeleteSub = 17, WireNew = 18, Circuit = 19
}

/// <summary>
/// Wire layout: kind u8, actor u8, rev v, baseRev v, count v, ids (first v, then ascending deltas v), payloadLen v, payload.
/// Touched ids are strictly ascending, so deltas are small and always positive.
/// </summary>
public static class OpFrame
{
    public const int MaxTouched = 4096;
    public const int MaxPayload = 1 << 20;

    public readonly ref struct View
    {
        public readonly OpKind Kind;
        public readonly byte Actor;
        public readonly ulong Rev;
        public readonly ulong BaseRev;
        public readonly ushort NativeEventId;
        public readonly int Count;
        public readonly ReadOnlySpan<byte> IdBytes;
        public readonly ReadOnlySpan<byte> Payload;
        internal View(OpKind kind, byte actor, ulong rev, ulong baseRev, ushort nativeEventId, int count, ReadOnlySpan<byte> ids, ReadOnlySpan<byte> payload)
        { Kind = kind; Actor = actor; Rev = rev; BaseRev = baseRev; NativeEventId = nativeEventId; Count = count; IdBytes = ids; Payload = payload; }

        /// <summary>Copies the touched ids into dest (length must be at least Count).</summary>
        public void ReadIds(Span<ushort> dest)
        {
            int pos = 0; uint prev = 0;
            for (int i = 0; i < Count; i++)
            {
                uint d = (uint)VarInt.Read(IdBytes, ref pos);
                prev = i == 0 ? d : prev + d;
                dest[i] = (ushort)prev;
            }
        }
    }

    public static bool AreStrictlyAscending(ReadOnlySpan<ushort> ids)
    {
        for (int i = 1; i < ids.Length; i++) { if (ids[i] <= ids[i - 1]) { return false; } }
        return true;
    }

    public static int MaxEncodedSize(int count, int payload) => 2 + 10 * 3 + 3 + 3 * count + 5 + payload;

    public static int Write(Span<byte> dest, OpKind kind, byte actor, ulong rev, ulong baseRev, ReadOnlySpan<ushort> ids, ReadOnlySpan<byte> payload, ushort nativeEventId = 0)
    {
        dest[0] = (byte)kind; dest[1] = actor;
        int pos = 2;
        VarInt.Write(dest, ref pos, rev);
        VarInt.Write(dest, ref pos, baseRev);
        VarInt.Write(dest, ref pos, nativeEventId);
        VarInt.Write(dest, ref pos, (ulong)ids.Length);
        for (int i = 0; i < ids.Length; i++) { VarInt.Write(dest, ref pos, i == 0 ? ids[0] : (uint)(ids[i] - ids[i - 1])); }
        VarInt.Write(dest, ref pos, (ulong)payload.Length);
        payload.CopyTo(dest.Slice(pos));
        return pos + payload.Length;
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out View view)
    {
        view = default;
        if (src.Length < 6) { return false; }
        var kind = (OpKind)src[0]; byte actor = src[1];
        int pos = 2;
        if (!VarInt.TryRead(src, ref pos, out ulong rev) || !VarInt.TryRead(src, ref pos, out ulong baseRev) ||
            !VarInt.TryRead(src, ref pos, out ulong nativeEventId) || nativeEventId > ushort.MaxValue || !VarInt.TryRead(src, ref pos, out ulong count)) { return false; }
        if (count > MaxTouched || baseRev > rev) { return false; }
        int idStart = pos;
        uint prev = 0;
        for (ulong i = 0; i < count; i++)
        {
            if (!VarInt.TryRead(src, ref pos, out ulong d)) { return false; }
            if (i > 0 && d == 0) { return false; }
            if (d > ushort.MaxValue) { return false; }
            prev = i == 0 ? (uint)d : prev + (uint)d;
            if (prev > ushort.MaxValue) { return false; }
        }
        int idEnd = pos;
        if (!VarInt.TryRead(src, ref pos, out ulong plen) || plen > MaxPayload || (ulong)(src.Length - pos) != plen) { return false; }
        view = new View(kind, actor, rev, baseRev, (ushort)nativeEventId, (int)count, src.Slice(idStart, idEnd - idStart), src.Slice(pos));
        return true;
    }
}

public static class VarInt
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> dest, ref int pos, ulong v)
    {
        while (v >= 0x80) { dest[pos++] = (byte)(v | 0x80); v >>= 7; }
        dest[pos++] = (byte)v;
    }

    public static bool TryRead(ReadOnlySpan<byte> src, ref int pos, out ulong v)
    {
        v = 0; int shift = 0;
        while (pos < src.Length && shift <= 63)
        {
            byte b = src[pos++];
            if (shift == 63 && b > 1) { return false; }
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) { return true; }
            shift += 7;
        }
        return false;
    }

    /// <summary>Unchecked read for spans already validated by OpFrame.TryRead.</summary>
    public static ulong Read(ReadOnlySpan<byte> src, ref int pos) { TryRead(src, ref pos, out ulong v); return v; }
}
