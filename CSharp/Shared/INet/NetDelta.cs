using System;
using System.Runtime.CompilerServices;
using Barotrauma.Networking;

namespace INet {
    public static class NetDelta {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WriteFloatDelta(IWriteMessage msg, ReadOnlySpan<float> current, ReadOnlySpan<float> baseline, float epsilon) {
            int len = current.Length;
            NativeWire.WriteVarUInt32(msg, (uint)len);
            int chunks = (len + 63) / 64;
            int changed = 0;

            for (int c = 0; c < chunks; c++) {
                int start = c * 64;
                int count = Math.Min(64, len - start);
                ulong mask = 0;
                for (int i = 0; i < count; i++) {
                    float diff = Math.Abs(current[start + i] - baseline[start + i]);
                    if (diff > epsilon) {
                        mask |= 1UL << i;
                    }
                }
                msg.WriteUInt64(mask);
                if (mask == 0) { continue; }
                for (int i = 0; i < count; i++) {
                    if ((mask & (1UL << i)) != 0) {
                        NativeWire.WriteHalf(msg, current[start + i]);
                        changed++;
                    }
                }
            }
            return changed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadFloatDelta(IReadMessage msg, Span<float> target) {
            int len = (int)NativeWire.ReadVarUInt32(msg);
            int chunks = (len + 63) / 64;
            int updated = 0;

            for (int c = 0; c < chunks; c++) {
                int start = c * 64;
                int count = Math.Min(64, len - start);
                ulong mask = msg.ReadUInt64();
                if (mask == 0) { continue; }
                for (int i = 0; i < count; i++) {
                    if ((mask & (1UL << i)) != 0) {
                        float val = NativeWire.ReadHalf(msg);
                        if (start + i < target.Length) {
                            target[start + i] = val;
                            updated++;
                        }
                    }
                }
            }
            return updated;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WriteByteDelta(IWriteMessage msg, ReadOnlySpan<byte> current, ReadOnlySpan<byte> baseline) {
            int len = current.Length;
            NativeWire.WriteVarUInt32(msg, (uint)len);
            int chunks = (len + 63) / 64;
            int changed = 0;

            for (int c = 0; c < chunks; c++) {
                int start = c * 64;
                int count = Math.Min(64, len - start);
                ulong mask = 0;
                for (int i = 0; i < count; i++) {
                    if (current[start + i] != baseline[start + i]) {
                        mask |= 1UL << i;
                    }
                }
                msg.WriteUInt64(mask);
                if (mask == 0) { continue; }
                for (int i = 0; i < count; i++) {
                    if ((mask & (1UL << i)) != 0) {
                        msg.WriteByte(current[start + i]);
                        changed++;
                    }
                }
            }
            return changed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadByteDelta(IReadMessage msg, Span<byte> target) {
            int len = (int)NativeWire.ReadVarUInt32(msg);
            int chunks = (len + 63) / 64;
            int updated = 0;

            for (int c = 0; c < chunks; c++) {
                int start = c * 64;
                int count = Math.Min(64, len - start);
                ulong mask = msg.ReadUInt64();
                if (mask == 0) { continue; }
                for (int i = 0; i < count; i++) {
                    if ((mask & (1UL << i)) != 0) {
                        byte val = msg.ReadByte();
                        if (start + i < target.Length) {
                            target[start + i] = val;
                            updated++;
                        }
                    }
                }
            }
            return updated;
        }
    }
}
