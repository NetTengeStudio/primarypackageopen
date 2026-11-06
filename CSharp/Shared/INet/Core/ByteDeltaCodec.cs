using System;
using System.Buffers.Binary;
using System.Numerics;

namespace INet;

public readonly record struct ByteStateChange(ushort Slot, byte Value);

public static class ByteDeltaCodec
{
    public const int PageSize = 256;
    public const int HeaderSize = 7;
    public const int MaxEncodedSize = HeaderSize + PageSize * 2;

    public static int Encode(Span<byte> output, int start, ReadOnlySpan<byte> values, ReadOnlySpan<ulong> selected, bool compact = true)
        => EncodePage(output, start, values, selected, out _, compact);

    internal static int EncodePage(Span<byte> output, int start, ReadOnlySpan<byte> values, ReadOnlySpan<ulong> selected, out int entries, bool compact)
    {
        entries = 0;
        if (values.Length is < 1 or > PageSize || (uint)start > ushort.MaxValue || start + values.Length > ushort.MaxValue || selected.Length != (values.Length + 63) / 64) { throw new ArgumentException("Invalid delta page"); }
        Span<byte> indices = stackalloc byte[PageSize];
        int count = 0, min = byte.MaxValue, max = 0;
        for (int word = 0; word < selected.Length; word++)
        {
            ulong mask = selected[word];
            if (word == selected.Length - 1 && (values.Length & 63) != 0 && mask >> (values.Length & 63) != 0) { throw new ArgumentException("Selection exceeds page"); }
            while (mask != 0)
            {
                int index = word * 64 + BitOperations.TrailingZeroCount(mask);
                indices[count++] = (byte)index;
                int value = values[index];
                min = Math.Min(min, value); max = Math.Max(max, value);
                mask &= mask - 1;
            }
        }
        if (count == 0) { return 0; }
        entries = count;
        int width = compact ? max == min ? 0 : BitOperations.Log2((uint)(max - min)) + 1 : 8;
        if (!compact) { min = 0; }
        int mode = compact && count == values.Length ? 0 : compact && values.Length <= count * 8 ? 1 : 2;
        int indexBits = mode == 0 ? 0 : mode == 1 ? values.Length : count * 8;
        int length = HeaderSize + (indexBits + count * width + 7) / 8;
        if (output.Length < length) { throw new ArgumentException("Delta output is too small"); }
        output[0] = (byte)(mode | width << 2);
        output[1] = (byte)(values.Length - 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(2), (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(4), (ushort)start);
        output[6] = (byte)min;
        var writer = new BitWriter(output.Slice(HeaderSize, length - HeaderSize));
        if (mode == 1)
        {
            for (int bit = 0; bit < values.Length; bit += 32)
            {
                int bits = Math.Min(32, values.Length - bit);
                uint mask = (uint)(selected[bit >> 6] >> (bit & 63));
                if (bits < 32) { mask &= (1u << bits) - 1; }
                writer.TryWrite(mask, bits);
            }
        }
        else if (mode == 2) { for (int i = 0; i < count; i++) { writer.TryWrite(indices[i], 8); } }
        if (width != 0) { for (int i = 0; i < count; i++) { writer.TryWrite((uint)(values[indices[i]] - min), width); } }
        return length;
    }

    public static bool TryDecode(ReadOnlySpan<byte> input, Span<ByteStateChange> changes, out int count, out int start, out int range)
    {
        count = start = range = 0;
        if (input.Length < HeaderSize) { return false; }
        int mode = input[0] & 3, width = input[0] >> 2;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(2));
        int first = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(4));
        int size = input[1] + 1;
        if (mode > 2 || width > 8 || n < 1 || n > size || n > changes.Length || first + size > ushort.MaxValue || mode == 0 && n != size) { return false; }
        int indexBits = mode == 0 ? 0 : mode == 1 ? size : n * 8;
        int bits = indexBits + n * width;
        if (input.Length != HeaderSize + (bits + 7) / 8) { return false; }
        var reader = new BitReader(input.Slice(HeaderSize));
        Span<byte> indices = stackalloc byte[PageSize];
        if (mode == 0) { for (int i = 0; i < n; i++) { indices[i] = (byte)i; } }
        else if (mode == 1)
        {
            int found = 0;
            for (int bit = 0; bit < size; bit += 32)
            {
                reader.TryRead(Math.Min(32, size - bit), out uint mask);
                while (mask != 0)
                {
                    if (found >= n) { return false; }
                    indices[found++] = (byte)(bit + BitOperations.TrailingZeroCount(mask));
                    mask &= mask - 1;
                }
            }
            if (found != n) { return false; }
        }
        else
        {
            int previous = -1;
            for (int i = 0; i < n; i++)
            {
                reader.TryRead(8, out uint index);
                if (index >= size || index <= previous) { return false; }
                indices[i] = (byte)index; previous = (int)index;
            }
        }
        for (int i = 0; i < n; i++)
        {
            reader.TryRead(width, out uint delta);
            uint value = input[6] + delta;
            if (value > byte.MaxValue) { return false; }
            changes[i] = new ByteStateChange((ushort)(first + indices[i]), (byte)value);
        }
        reader.TryRead((8 - (bits & 7)) & 7, out uint padding);
        if (padding != 0) { return false; }
        count = n; start = first; range = size;
        return true;
    }
}
