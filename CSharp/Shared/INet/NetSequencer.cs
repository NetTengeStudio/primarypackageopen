using System;
using System.Runtime.CompilerServices;
using Barotrauma.Networking;

namespace INet {
    public sealed class NetSequencer {
        private const int WindowSize = 64;
        private ushort localSequence;
        private ushort remoteSequence;
        private ulong remoteAckMask;
        private float smoothedRtt = 0.05f;
        private float rttVariance = 0.01f;
        private readonly float[] sentTimestamps = new float[1024];

        public ushort NextSequence => ++localSequence;
        public ushort RemoteSequence => remoteSequence;
        public ulong AckMask => remoteAckMask;
        public float Rtt => smoothedRtt;
        public float Jitter => rttVariance;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteHeader(IWriteMessage msg, float currentTime) {
            ushort seq = NextSequence;
            sentTimestamps[seq & 1023] = currentTime;
            msg.WriteUInt16(seq);
            msg.WriteUInt16(remoteSequence);
            msg.WriteUInt64(remoteAckMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadHeader(IReadMessage msg, float currentTime, out ushort incomingSeq, out ushort ackedSeq) {
            incomingSeq = msg.ReadUInt16();
            ackedSeq = msg.ReadUInt16();
            ulong incomingAckMask = msg.ReadUInt64();

            float sentTime = sentTimestamps[ackedSeq & 1023];
            if (sentTime > 0.0f && currentTime >= sentTime) {
                float sample = currentTime - sentTime;
                smoothedRtt += (sample - smoothedRtt) * 0.1f;
                rttVariance += (Math.Abs(sample - smoothedRtt) - rttVariance) * 0.1f;
            }

            int diff = (short)(incomingSeq - remoteSequence);
            if (diff > 0) {
                if (diff < WindowSize) {
                    remoteAckMask = (remoteAckMask << diff) | (1UL << (diff - 1));
                } else {
                    remoteAckMask = 0;
                }
                remoteSequence = incomingSeq;
                return true;
            } else if (diff < 0) {
                int bitIndex = -diff - 1;
                if (bitIndex < WindowSize) {
                    remoteAckMask |= 1UL << bitIndex;
                }
                return false;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAcked(ushort seq) {
            int diff = (short)(remoteSequence - seq);
            if (diff == 0) { return true; }
            if (diff > 0 && diff <= WindowSize) {
                return (remoteAckMask & (1UL << (diff - 1))) != 0;
            }
            return false;
        }

        public void Reset() {
            localSequence = 0;
            remoteSequence = 0;
            remoteAckMask = 0;
            smoothedRtt = 0.05f;
            rttVariance = 0.01f;
            Array.Clear(sentTimestamps, 0, sentTimestamps.Length);
        }
    }
}
