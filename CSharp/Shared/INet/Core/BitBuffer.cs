using System;

namespace INet;

public ref struct BufferBitWriter
{
    readonly Span<byte> buffer;
    public int Position { get; private set; }
    public BufferBitWriter(Span<byte> buffer) { this.buffer = buffer; Position = 0; buffer.Clear(); }
    public bool TryWrite(uint value, int bits)
    {
        if ((uint)bits > 32 || bits < 32 && value >> bits != 0 || Position > int.MaxValue - bits || (long)Position + bits > (long)buffer.Length * 8) { return false; }
        if (bits == 0) { return true; }
        int first = Position >> 3, offset = Position & 7;
        ulong packed = (ulong)value << offset;
        int bytes = (offset + bits + 7) >> 3;
        for (int i = 0; i < bytes; i++) { buffer[first + i] |= (byte)(packed >> (i * 8)); }
        Position += bits;
        return true;
    }
}

public ref struct BufferBitReader
{
    readonly ReadOnlySpan<byte> buffer;
    public int Position { get; private set; }
    public BufferBitReader(ReadOnlySpan<byte> buffer) { this.buffer = buffer; Position = 0; }
    public bool TryRead(int bits, out uint value)
    {
        value = 0;
        if ((uint)bits > 32 || Position > int.MaxValue - bits || (long)Position + bits > (long)buffer.Length * 8) { return false; }
        if (bits == 0) { return true; }
        int first = Position >> 3, offset = Position & 7, bytes = (offset + bits + 7) >> 3;
        ulong packed = 0;
        for (int i = 0; i < bytes; i++) { packed |= (ulong)buffer[first + i] << (i * 8); }
        value = (uint)(packed >> offset);
        if (bits < 32) { value &= (1u << bits) - 1u; }
        Position += bits;
        return true;
    }
}
