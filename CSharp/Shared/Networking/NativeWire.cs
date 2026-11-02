using System;
using Barotrauma.Networking;

namespace PrimaryPackage.Networking;

internal static class NativeWire {
    internal const int MaxFrameBytes = 8192;
    internal static void Write(IWriteMessage output, byte[] buffer, int length) {
        if (length is < 1 or > MaxFrameBytes || length > buffer.Length) { throw new ArgumentException("Invalid world wire frame length"); }
        output.WriteUInt16((ushort)length); output.WritePadBits(); output.WriteBytes(buffer, 0, length);
    }
    // Borrow only during this native receive callback; no intermediate byte[] allocation.
    internal static ReadOnlySpan<byte> Read(IReadMessage input) {
        int available = input.LengthBits;
        if (available - input.BitPosition < 16) { throw new ArgumentException("Truncated world wire header"); }
        int length = input.ReadUInt16(); input.ReadPadBits(); int first = input.BytePosition;
        if (length is < 1 or > MaxFrameBytes || available - input.BitPosition < length * 8 || first > input.Buffer.Length - length) { throw new ArgumentException("Truncated or oversized world wire frame"); }
        input.BitPosition += length * 8;
        return input.Buffer.AsSpan(first, length);
    }
}
