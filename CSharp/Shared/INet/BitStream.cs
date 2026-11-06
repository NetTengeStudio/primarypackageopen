using System;
using System.Runtime.CompilerServices;
using Barotrauma.Networking;

namespace INet {
    public ref struct BitWriter {
        private readonly IWriteMessage output;
        private readonly Span<byte> span;
        private readonly bool isSpan;
        private ulong accumulator;
        private int bitCount;
        private int bitPos;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BitWriter(IWriteMessage outMsg) {
            output = outMsg;
            span = default;
            isSpan = false;
            accumulator = 0;
            bitCount = 0;
            bitPos = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BitWriter(Span<byte> destination) {
            output = null;
            span = destination;
            isSpan = true;
            accumulator = 0;
            bitCount = 0;
            bitPos = 0;
        }

        public int BitPosition => isSpan ? bitPos : (output != null ? output.BitPosition : 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryWrite(uint value, int bits) {
            if (bits == 0) return true;
            if (isSpan) {
                if (bitPos + bits > span.Length * 8) return false;
                while (bits > 0) {
                    int byteIndex = bitPos >> 3;
                    int bitOffset = bitPos & 7;
                    int bitsCanWrite = Math.Min(bits, 8 - bitOffset);
                    uint chunkMask = (1u << bitsCanWrite) - 1;
                    uint chunk = (value >> (bits - bitsCanWrite)) & chunkMask;

                    span[byteIndex] = (byte)((span[byteIndex] & ~(chunkMask << bitOffset)) | (chunk << bitOffset));

                    bitPos += bitsCanWrite;
                    bits -= bitsCanWrite;
                }
                return true;
            }
            if (output != null) {
                WriteBits(value, bits);
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBits(ulong value, int bits) {
            if (bits <= 0) { return; }
            if (isSpan) {
                TryWrite((uint)value, bits);
                return;
            }
            ulong mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1UL;
            value &= mask;

            if (bitCount + bits <= 64) {
                accumulator |= value << bitCount;
                bitCount += bits;
                if (bitCount == 64) {
                    FlushWord();
                }
            } else {
                int first = 64 - bitCount;
                accumulator |= (value & ((1UL << first) - 1UL)) << bitCount;
                bitCount = 64;
                FlushWord();
                int remaining = bits - first;
                accumulator = value >> first;
                bitCount = remaining;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBool(bool value) => WriteBits(value ? 1UL : 0UL, 1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRangedInt(int value, int min, int max) {
            uint spanVal = (uint)(max - min);
            int bits = 32 - System.Numerics.BitOperations.LeadingZeroCount(spanVal);
            if (bits == 0) { bits = 1; }
            uint offset = (uint)Math.Clamp(value - min, 0, (int)spanVal);
            WriteBits(offset, bits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRangedFloat(float value, float min, float max, int bits) {
            float norm = Math.Clamp((value - min) / (max - min), 0.0f, 1.0f);
            ulong maxVal = (1UL << bits) - 1UL;
            ulong quantized = (ulong)(norm * maxVal + 0.5f);
            WriteBits(quantized, bits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Flush() {
            if (isSpan || output == null) return;
            while (bitCount > 0) {
                output.WriteByte((byte)(accumulator & 0xFF));
                accumulator >>= 8;
                bitCount -= 8;
            }
            bitCount = 0;
            accumulator = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FlushWord() {
            if (output == null) return;
            output.WriteUInt64(accumulator);
            accumulator = 0;
            bitCount = 0;
        }
    }

    public ref struct BitReader {
        private readonly IReadMessage input;
        private readonly ReadOnlySpan<byte> span;
        private readonly bool isSpan;
        private ulong accumulator;
        private int bitCount;
        private int bitPos;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BitReader(IReadMessage inMsg) {
            input = inMsg;
            span = default;
            isSpan = false;
            accumulator = 0;
            bitCount = 0;
            bitPos = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BitReader(ReadOnlySpan<byte> source) {
            input = null;
            span = source;
            isSpan = true;
            accumulator = 0;
            bitCount = 0;
            bitPos = 0;
        }

        public int BitPosition => isSpan ? bitPos : (input != null ? input.BitPosition : 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRead(int bits, out uint value) {
            value = 0;
            if (bits == 0) return true;
            if (isSpan) {
                if (bitPos + bits > span.Length * 8) return false;
                while (bits > 0) {
                    int byteIndex = bitPos >> 3;
                    int bitOffset = bitPos & 7;
                    int bitsCanRead = Math.Min(bits, 8 - bitOffset);
                    uint chunkMask = (1u << bitsCanRead) - 1;
                    uint chunk = (uint)((span[byteIndex] >> bitOffset) & chunkMask);

                    value = (value << bitsCanRead) | chunk;

                    bitPos += bitsCanRead;
                    bits -= bitsCanRead;
                }
                return true;
            }
            if (input != null) {
                if (input.BitPosition + bits > input.LengthBits) return false;
                value = (uint)ReadBits(bits);
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadBits(int bits) {
            if (bits <= 0) { return 0; }
            if (isSpan) {
                if (TryRead(bits, out uint val)) return val;
                return 0;
            }
            while (bitCount < bits && input.BitPosition < input.LengthBits) {
                accumulator |= ((ulong)input.ReadByte()) << bitCount;
                bitCount += 8;
            }

            ulong mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1UL;
            ulong result = accumulator & mask;
            accumulator >>= bits;
            bitCount = Math.Max(0, bitCount - bits);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadBool() => ReadBits(1) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadRangedInt(int min, int max) {
            uint spanVal = (uint)(max - min);
            int bits = 32 - System.Numerics.BitOperations.LeadingZeroCount(spanVal);
            if (bits == 0) { bits = 1; }
            uint offset = (uint)ReadBits(bits);
            return min + (int)Math.Min(offset, spanVal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ReadRangedFloat(float min, float max, int bits) {
            ulong maxVal = (1UL << bits) - 1UL;
            ulong val = ReadBits(bits);
            float norm = (float)val / maxVal;
            return min + norm * (max - min);
        }
    }
}
