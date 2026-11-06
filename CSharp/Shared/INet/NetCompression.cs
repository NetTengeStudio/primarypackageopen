using System;
using System.Runtime.CompilerServices;
using Barotrauma.Networking;

namespace INet {
    public static class NetCompression {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PackNibbles(ReadOnlySpan<byte> source, Span<byte> destination) {
            int len = source.Length;
            int destIdx = 0;
            for (int i = 0; i < len; i += 2) {
                byte low = (byte)(source[i] & 0x0F);
                byte high = (i + 1 < len) ? (byte)((source[i + 1] & 0x0F) << 4) : (byte)0;
                destination[destIdx++] = (byte)(low | high);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnpackNibbles(ReadOnlySpan<byte> source, Span<byte> destination, int elementCount) {
            int srcIdx = 0;
            for (int i = 0; i < elementCount; i += 2) {
                byte b = source[srcIdx++];
                destination[i] = (byte)(b & 0x0F);
                if (i + 1 < elementCount) {
                    destination[i + 1] = (byte)((b >> 4) & 0x0F);
                }
            }
        }

        public static int CompressRle(ReadOnlySpan<byte> input, Span<byte> output) {
            int inLen = input.Length;
            if (inLen == 0) { return 0; }
            int outIdx = 0;
            int i = 0;
            while (i < inLen) {
                byte val = input[i];
                int run = 1;
                while (i + run < inLen && input[i + run] == val && run < 255) {
                    run++;
                }
                if (run >= 3 || val == 0xAA) {
                    if (outIdx + 3 > output.Length) { return -1; }
                    output[outIdx++] = 0xAA;
                    output[outIdx++] = (byte)run;
                    output[outIdx++] = val;
                } else {
                    for (int r = 0; r < run; r++) {
                        if (outIdx >= output.Length) { return -1; }
                        output[outIdx++] = val;
                    }
                }
                i += run;
            }
            return outIdx;
        }

        public static int DecompressRle(ReadOnlySpan<byte> input, Span<byte> output) {
            int inLen = input.Length;
            int outIdx = 0;
            int i = 0;
            while (i < inLen) {
                byte b = input[i++];
                if (b == 0xAA) {
                    if (i + 1 >= inLen) { break; }
                    int run = input[i++];
                    byte val = input[i++];
                    for (int r = 0; r < run; r++) {
                        if (outIdx >= output.Length) { return -1; }
                        output[outIdx++] = val;
                    }
                } else {
                    if (outIdx >= output.Length) { return -1; }
                    output[outIdx++] = b;
                }
            }
            return outIdx;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRleMessage(IWriteMessage msg, ReadOnlySpan<byte> source) {
            Span<byte> temp = stackalloc byte[Math.Min(1024, source.Length * 2 + 16)];
            int compLen = CompressRle(source, temp);
            if (compLen > 0 && compLen < source.Length) {
                msg.WriteBoolean(true);
                NativeWire.WriteVarUInt32(msg, (uint)source.Length);
                NativeWire.WriteVarUInt32(msg, (uint)compLen);
                for (int i = 0; i < compLen; i++) {
                    msg.WriteByte(temp[i]);
                }
            } else {
                msg.WriteBoolean(false);
                NativeWire.WriteVarUInt32(msg, (uint)source.Length);
                for (int i = 0; i < source.Length; i++) {
                    msg.WriteByte(source[i]);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadRleMessage(IReadMessage msg, Span<byte> destination) {
            bool isCompressed = msg.ReadBoolean();
            int origLen = (int)NativeWire.ReadVarUInt32(msg);
            if (origLen > destination.Length) { return -1; }
            if (isCompressed) {
                int compLen = (int)NativeWire.ReadVarUInt32(msg);
                Span<byte> compBuf = stackalloc byte[Math.Min(1024, compLen)];
                for (int i = 0; i < compLen; i++) {
                    compBuf[i] = msg.ReadByte();
                }
                return DecompressRle(compBuf.Slice(0, compLen), destination.Slice(0, origLen));
            } else {
                for (int i = 0; i < origLen; i++) {
                    destination[i] = msg.ReadByte();
                }
                return origLen;
            }
        }
    }
}