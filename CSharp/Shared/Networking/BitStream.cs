using System;
using System.Buffers.Binary;
using System.Text;

namespace PrimaryPackage.Networking;

// Bounded, little-endian wire representation. No host layout, reflection, unsafe or
// per-field buffers. The caller owns storage and its lifetime; spans never escape.
internal ref struct BitWriter {
    private readonly Span<byte> buffer;
    private int position;
    internal int LengthBits => position;
    internal int LengthBytes => (position + 7) >> 3;
    internal BitWriter(Span<byte> buffer) { if (buffer.Length > int.MaxValue / 8) { throw new ArgumentException("Wire output too large"); } this.buffer = buffer; position = 0; }
    internal void Bits(ulong value, int count) {
        if ((uint)count > 64 || count < 64 && value >> count != 0) { throw new ArgumentOutOfRangeException(nameof(count)); }
        Require(count);
        if ((position & 7) == 0 && count == 64) { BinaryPrimitives.WriteUInt64LittleEndian(buffer[(position >> 3)..], value); position += 64; return; }
        while (count > 0) {
            int offset = position & 7, take = Math.Min(8 - offset, count), index = position >> 3;
            int mask = (1 << take) - 1;
            if (offset == 0) { buffer[index] = 0; }
            buffer[index] = (byte)((buffer[index] & ~(mask << offset)) | ((int)(value & (uint)mask) << offset));
            value >>= take; position += take; count -= take;
        }
    }
    internal void Align() { int padding = (-position) & 7; if (padding != 0) { Bits(0, padding); } }
    internal void UInt(ulong value) {
        Align();
        while (value >= 128) { Require(8); buffer[position >> 3] = (byte)(value | 128); position += 8; value >>= 7; }
        Require(8); buffer[position >> 3] = (byte)value; position += 8;
    }
    internal void Int(int value) => UInt(unchecked((uint)((value << 1) ^ (value >> 31))));
    internal void Single(float value) {
        if (!float.IsFinite(value)) { throw new ArgumentException("Non-finite wire number"); }
        Align(); Require(32); BinaryPrimitives.WriteSingleLittleEndian(buffer[(position >> 3)..], value); position += 32;
    }
    internal void Guid(Guid value) {
        Align(); Require(128);
        if (!value.TryWriteBytes(buffer[(position >> 3)..])) { throw new ArgumentException("Wire GUID destination too small"); }
        position += 128;
    }
    internal void Bytes(ReadOnlySpan<byte> value) { Align(); RequireBytes(value.Length); value.CopyTo(buffer[(position >> 3)..]); position += value.Length * 8; }
    internal void Text(string value, int maxBytes) {
        int count = WireEncoding.Utf8.GetByteCount(value);
        if (count > maxBytes) { throw new ArgumentException("Wire string exceeds limit"); }
        UInt((uint)count); RequireBytes(count);
        WireEncoding.Utf8.GetBytes(value.AsSpan(), buffer[(position >> 3)..]); position += count * 8;
    }
    private void Require(int bits) { if (bits > (long)buffer.Length * 8 - position) { throw new ArgumentException("Wire output exceeds capacity"); } }
    private void RequireBytes(int count) { if (count < 0 || count > buffer.Length - (position >> 3)) { throw new ArgumentException("Wire output exceeds capacity"); } }
}

internal ref struct BitReader {
    private readonly ReadOnlySpan<byte> buffer;
    private int position;
    internal int RemainingBits => buffer.Length * 8 - position;
    internal BitReader(ReadOnlySpan<byte> buffer) { if (buffer.Length > int.MaxValue / 8) { throw new ArgumentException("Wire input too large"); } this.buffer = buffer; position = 0; }
    internal ulong Bits(int count) {
        if ((uint)count > 64) { throw new ArgumentOutOfRangeException(nameof(count)); }
        Require(count);
        if ((position & 7) == 0 && count == 64) { ulong result = BinaryPrimitives.ReadUInt64LittleEndian(buffer[(position >> 3)..]); position += 64; return result; }
        ulong value = 0; int shift = 0;
        while (count > 0) {
            int offset = position & 7, take = Math.Min(8 - offset, count);
            value |= (ulong)((buffer[position >> 3] >> offset) & ((1 << take) - 1)) << shift;
            position += take; shift += take; count -= take;
        }
        return value;
    }
    internal void Align() { int padding = (-position) & 7; if (padding != 0 && Bits(padding) != 0) { throw new ArgumentException("Non-canonical wire padding"); } }
    internal ulong UInt() {
        Align(); ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7) {
            Require(8); byte part = buffer[position >> 3]; position += 8;
            if (shift == 63 && part > 1) { throw new ArgumentException("Wire integer overflow"); }
            result |= (ulong)(part & 127) << shift;
            if (part < 128) {
                if (shift != 0 && part == 0) { throw new ArgumentException("Non-canonical wire integer"); }
                return result;
            }
        }
        throw new ArgumentException("Wire integer overflow");
    }
    internal uint UInt32() { ulong value = UInt(); return value <= uint.MaxValue ? (uint)value : throw new ArgumentException("Wire UInt32 overflow"); }
    internal int Int() { uint value = UInt32(); return unchecked((int)(value >> 1) ^ -(int)(value & 1)); }
    internal float Single() {
        Align(); Require(32); float result = BinaryPrimitives.ReadSingleLittleEndian(buffer[(position >> 3)..]); position += 32;
        return float.IsFinite(result) ? result : throw new ArgumentException("Non-finite wire number");
    }
    internal Guid Guid() => new(Bytes(16));
    internal ReadOnlySpan<byte> Bytes(int count) {
        Align();
        if (count < 0 || count > (buffer.Length - (position >> 3))) { throw new ArgumentException("Truncated wire bytes"); }
        var result = buffer.Slice(position >> 3, count); position += count * 8; return result;
    }
    internal string Text(int maxBytes) {
        uint count = UInt32(); if (count > maxBytes) { throw new ArgumentException("Wire string exceeds limit"); }
        return WireEncoding.Utf8.GetString(Bytes((int)count));
    }
    internal void Finish() { Align(); if (RemainingBits != 0) { throw new ArgumentException("Unexpected trailing wire data"); } }
    private void Require(int count) { if (count > RemainingBits) { throw new ArgumentException("Truncated wire bits"); } }
}

internal static class WireEncoding {
    internal static readonly UTF8Encoding Utf8 = new(false, true);
}
