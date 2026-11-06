using System;
using System.Runtime.CompilerServices;
using Barotrauma.Networking;

namespace INet {
    public static class NativeWire {
        public const int MaxFrameBytes = 1024;
        private static readonly ushort[] CrcTable = new ushort[256];

        static NativeWire() {
            const ushort poly = 0xA001;
            for (ushort i = 0; i < 256; i++) {
                ushort val = i;
                for (int j = 0; j < 8; j++) {
                    val = (val & 1) != 0 ? (ushort)((val >> 1) ^ poly) : (ushort)(val >> 1);
                }
                CrcTable[i] = val;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ComputeCrc16(ReadOnlySpan<byte> span) {
            ushort crc = 0xFFFF;
            for (int i = 0; i < span.Length; i++) {
                crc = (ushort)((crc >> 8) ^ CrcTable[(crc ^ span[i]) & 0xFF]);
            }
            return crc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(IWriteMessage output, byte[] buffer, int length) {
            if (buffer == null || length <= 0) {
                WriteVarUInt32(output, 0);
                return;
            }
            WriteVarUInt32(output, (uint)length);
            output.WriteBytes(buffer, 0, length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(IWriteMessage output, ReadOnlySpan<byte> span) {
            WriteVarUInt32(output, (uint)span.Length);
            for (int i = 0; i < span.Length; i++) {
                output.WriteByte(span[i]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read(IReadMessage input, byte[] target) {
            uint len = ReadVarUInt32(input);
            if (len == 0 || target == null) { return 0; }
            int count = (int)Math.Min(len, (uint)target.Length);
            for (int i = 0; i < count; i++) {
                target[i] = input.ReadByte();
            }
            int remaining = (int)len - count;
            for (int i = 0; i < remaining; i++) {
                input.ReadByte();
            }
            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVarUInt32(IWriteMessage msg, uint value) {
            while (value >= 0x80) {
                msg.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            msg.WriteByte((byte)value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadVarUInt32(IReadMessage msg) {
            uint result = 0;
            int shift = 0;
            while (shift < 35) {
                byte b = msg.ReadByte();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) { return result; }
                shift += 7;
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVarInt32(IWriteMessage msg, int value) =>
            WriteVarUInt32(msg, (uint)((value << 1) ^ (value >> 31)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadVarInt32(IReadMessage msg) {
            uint v = ReadVarUInt32(msg);
            return (int)(v >> 1) ^ -(int)(v & 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteHalf(IWriteMessage msg, float value) =>
            msg.WriteUInt16(BitConverter.HalfToUInt16Bits((Half)value));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ReadHalf(IReadMessage msg) =>
            (float)BitConverter.UInt16BitsToHalf(msg.ReadUInt16());
    }
}
