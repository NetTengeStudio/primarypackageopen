using System;
using System.Diagnostics;
using Barotrauma.Networking;

namespace INet {
    public static unsafe class NetPing {
        public const string ChannelId = "PP_INetPingV1";

        public struct PingPacket {
            public uint Sequence;
            public long ClientTimeUs;
            public long ServerTimeUs;
        }

        private static IDisposable listener;
#if CLIENT
        private static uint sequence;
#endif
        private static float currentRttMs;
        private static float averageRttMs;
        private static long clockOffsetUs;

        public static float CurrentRttMs => currentRttMs;
        public static float AverageRttMs => averageRttMs;
        public static long ClockOffsetUs => clockOffsetUs;

        public static void Initialize() {
            if (listener != null) { return; }
#if CLIENT
            listener = Net.Listen(ChannelId, OnReceiveClient);
#else
            listener = Net.Listen(ChannelId, OnReceiveServer);
#endif
        }

        public static void Release() {
            listener?.Dispose();
            listener = null;
#if CLIENT
            sequence = 0;
#endif
            currentRttMs = 0.0f;
            averageRttMs = 0.0f;
            clockOffsetUs = 0;
        }

#if CLIENT
        public static void SendProbe() {
            uint seq = ++sequence;
            long nowUs = Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;
            PingPacket pkt = new PingPacket {
                Sequence = seq,
                ClientTimeUs = nowUs,
                ServerTimeUs = 0
            };
            Net.SendBlit(ChannelId, in pkt, DeliveryMethod.Unreliable);
        }

        private static void OnReceiveClient(IReadMessage msg) {
            if (msg.LengthBytes < sizeof(PingPacket)) { return; }
            PingPacket pkt = default;
            byte* ptr = (byte*)&pkt;
            for (int i = 0; i < sizeof(PingPacket); i++) {
                ptr[i] = msg.ReadByte();
            }

            long nowUs = Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;
            long rttUs = nowUs - pkt.ClientTimeUs;
            if (rttUs < 0) { rttUs = 0; }

            currentRttMs = rttUs / 1000.0f;
            if (averageRttMs <= 0.0f) {
                averageRttMs = currentRttMs;
            } else {
                averageRttMs = averageRttMs * 0.85f + currentRttMs * 0.15f;
            }

            long expectedServerTime = pkt.ClientTimeUs + (rttUs / 2);
            clockOffsetUs = pkt.ServerTimeUs - expectedServerTime;
        }
#else
        private static void OnReceiveServer(IReadMessage msg, Client sender) {
            if (sender?.Connection == null || msg.LengthBytes < sizeof(PingPacket)) { return; }
            PingPacket pkt = default;
            byte* ptr = (byte*)&pkt;
            for (int i = 0; i < sizeof(PingPacket); i++) {
                ptr[i] = msg.ReadByte();
            }

            pkt.ServerTimeUs = Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;
            Net.SendBlit(ChannelId, sender.Connection, in pkt, DeliveryMethod.Unreliable);
        }
#endif
    }
}
