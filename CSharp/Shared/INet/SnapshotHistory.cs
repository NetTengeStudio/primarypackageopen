using System;
using System.Runtime.CompilerServices;

namespace INet {
    public unsafe sealed class SnapshotHistory<T> where T : unmanaged {
        private readonly struct SnapshotEntry {
            public readonly uint Tick;
            public readonly float Timestamp;
            public readonly T State;

            public SnapshotEntry(uint tick, float time, in T state) {
                Tick = tick;
                Timestamp = time;
                State = state;
            }
        }

        private const int Capacity = 128;
        private readonly SnapshotEntry[] ring = new SnapshotEntry[Capacity];
        private uint latestTick;

        public uint LatestTick => latestTick;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Record(uint tick, float timestamp, in T state) {
            ring[tick & (Capacity - 1)] = new SnapshotEntry(tick, timestamp, in state);
            if (tick > latestTick) { latestTick = tick; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(uint tick, out T state, out float timestamp) {
            int slot = (int)(tick & (Capacity - 1));
            if (ring[slot].Tick == tick) {
                state = ring[slot].State;
                timestamp = ring[slot].Timestamp;
                return true;
            }
            state = default;
            timestamp = 0.0f;
            return false;
        }

        public int ComputeDelta(uint baselineTick, uint targetTick, Span<byte> output) {
            if (!TryGet(baselineTick, out T baseState, out _) || !TryGet(targetTick, out T targetState, out _)) {
                return -1;
            }

            int words = (sizeof(T) + 7) / 8;
            ulong* pBase = (ulong*)&baseState;
            ulong* pTarget = (ulong*)&targetState;

            ulong mask = 0;
            for (int i = 0; i < Math.Min(64, words); i++) {
                if (pBase[i] != pTarget[i]) {
                    mask |= 1UL << i;
                }
            }

            int requiredBytes = 8 + System.Numerics.BitOperations.PopCount(mask) * 8;
            if (output.Length < requiredBytes) { return -1; }

            fixed (byte* pOut = output) {
                *(ulong*)pOut = mask;
                ulong* pData = (ulong*)(pOut + 8);
                int writeIdx = 0;
                for (int i = 0; i < Math.Min(64, words); i++) {
                    if ((mask & (1UL << i)) != 0) {
                        pData[writeIdx++] = pTarget[i];
                    }
                }
            }
            return requiredBytes;
        }

        public static bool ApplyDelta(in T baseState, ReadOnlySpan<byte> delta, out T result) {
            result = baseState;
            if (delta.Length < 8) { return false; }
            fixed (byte* pDelta = delta) {
                ulong mask = *(ulong*)pDelta;
                int words = (sizeof(T) + 7) / 8;
                int pop = System.Numerics.BitOperations.PopCount(mask);
                if (delta.Length < 8 + pop * 8) { return false; }

                fixed (T* pRes = &result) {
                    ulong* pWords = (ulong*)pRes;
                    ulong* pData = (ulong*)(pDelta + 8);
                    int readIdx = 0;
                    for (int i = 0; i < Math.Min(64, words); i++) {
                        if ((mask & (1UL << i)) != 0) {
                            pWords[i] = pData[readIdx++];
                        }
                    }
                }
            }
            return true;
        }
    }

    public sealed class NetInterpolator {
        private struct Sample {
            public float Timestamp;
            public float Value;
        }

        private const int WindowSize = 32;
        private readonly Sample[] samples = new Sample[WindowSize];
        private int head;
        private int count;

        public void AddSample(float timestamp, float value) => Push(timestamp, value);
        public float Evaluate(float renderTime) => SampleAt(renderTime);

        public void Push(float timestamp, float value) {
            samples[head] = new Sample { Timestamp = timestamp, Value = value };
            head = (head + 1) & (WindowSize - 1);
            if (count < WindowSize) { count++; }
        }

        public float SampleAt(float renderTime) {
            if (count == 0) { return 0f; }
            if (count == 1) { return samples[(head - 1 + WindowSize) & (WindowSize - 1)].Value; }

            int latestIdx = (head - 1 + WindowSize) & (WindowSize - 1);
            int oldestIdx = (head - count + WindowSize) & (WindowSize - 1);

            if (renderTime >= samples[latestIdx].Timestamp) { return samples[latestIdx].Value; }
            if (renderTime <= samples[oldestIdx].Timestamp) { return samples[oldestIdx].Value; }

            for (int i = 0; i < count - 1; i++) {
                int curr = (head - 1 - i + WindowSize) & (WindowSize - 1);
                int prev = (curr - 1 + WindowSize) & (WindowSize - 1);
                if (renderTime >= samples[prev].Timestamp && renderTime <= samples[curr].Timestamp) {
                    float duration = samples[curr].Timestamp - samples[prev].Timestamp;
                    if (duration <= 0.0001f) { return samples[curr].Value; }
                    float t = (renderTime - samples[prev].Timestamp) / duration;
                    return samples[prev].Value + (samples[curr].Value - samples[prev].Value) * t;
                }
            }

            return samples[latestIdx].Value;
        }

        public void Reset() {
            head = 0;
            count = 0;
        }
    }
}
