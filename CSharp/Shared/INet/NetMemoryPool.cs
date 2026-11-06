using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace INet {
    public static unsafe class NetMemoryPool {
        private const int SmallSlab = 128;
        private const int MediumSlab = 512;
        private const int LargeSlab = 2048;
        private const int SlabCount = 64;

        private static readonly byte*[] smallPool = new byte*[SlabCount];
        private static readonly byte*[] mediumPool = new byte*[SlabCount];
        private static readonly byte*[] largePool = new byte*[SlabCount];
        private static int smallHead;
        private static int mediumHead;
        private static int largeHead;

        static NetMemoryPool() {
            for (int i = 0; i < SlabCount; i++) {
                smallPool[i] = (byte*)NativeMemory.Alloc(SmallSlab);
                mediumPool[i] = (byte*)NativeMemory.Alloc(MediumSlab);
                largePool[i] = (byte*)NativeMemory.Alloc(LargeSlab);
            }
            smallHead = SlabCount;
            mediumHead = SlabCount;
            largeHead = SlabCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NativeSpan Rent(int minimumSize) {
            if (minimumSize <= SmallSlab) {
                int idx = Interlocked.Decrement(ref smallHead);
                if (idx >= 0) {
                    return new NativeSpan(smallPool[idx], SmallSlab, 1);
                }
                Interlocked.Increment(ref smallHead);
                return new NativeSpan((byte*)NativeMemory.Alloc((nuint)SmallSlab), SmallSlab, 0);
            }
            if (minimumSize <= MediumSlab) {
                int idx = Interlocked.Decrement(ref mediumHead);
                if (idx >= 0) {
                    return new NativeSpan(mediumPool[idx], MediumSlab, 2);
                }
                Interlocked.Increment(ref mediumHead);
                return new NativeSpan((byte*)NativeMemory.Alloc((nuint)MediumSlab), MediumSlab, 0);
            }
            if (minimumSize <= LargeSlab) {
                int idx = Interlocked.Decrement(ref largeHead);
                if (idx >= 0) {
                    return new NativeSpan(largePool[idx], LargeSlab, 3);
                }
                Interlocked.Increment(ref largeHead);
                return new NativeSpan((byte*)NativeMemory.Alloc((nuint)LargeSlab), LargeSlab, 0);
            }
            return new NativeSpan((byte*)NativeMemory.Alloc((nuint)minimumSize), minimumSize, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Return(in NativeSpan span) {
            if (span.Pointer == null) { return; }
            if (span.Category == 1) {
                int idx = Interlocked.Increment(ref smallHead) - 1;
                if (idx < SlabCount) {
                    smallPool[idx] = span.Pointer;
                    return;
                }
                Interlocked.Decrement(ref smallHead);
            } else if (span.Category == 2) {
                int idx = Interlocked.Increment(ref mediumHead) - 1;
                if (idx < SlabCount) {
                    mediumPool[idx] = span.Pointer;
                    return;
                }
                Interlocked.Decrement(ref mediumHead);
            } else if (span.Category == 3) {
                int idx = Interlocked.Increment(ref largeHead) - 1;
                if (idx < SlabCount) {
                    largePool[idx] = span.Pointer;
                    return;
                }
                Interlocked.Decrement(ref largeHead);
            }
            NativeMemory.Free(span.Pointer);
        }
    }

    public readonly unsafe ref struct NativeSpan {
        public readonly byte* Pointer;
        public readonly int Capacity;
        internal readonly byte Category;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal NativeSpan(byte* ptr, int cap, byte cat) {
            Pointer = ptr;
            Capacity = cap;
            Category = cat;
        }

        public Span<byte> Span {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new(Pointer, Capacity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() => NetMemoryPool.Return(in this);
    }
}