using System;
using Barotrauma;
using Barotrauma.Networking;
using INet;

namespace PrimaryPackage.Cold {
    public static class ColdClientNet {
        private static IDisposable listener;
        private static float requestCooldown;

        public static double LastReceivedAt { get; private set; } = double.NegativeInfinity;
        public static int LastReceivedHulls { get; private set; }
        public static int ReceivedMessages { get; private set; }
        public static bool Synced { get; private set; }

        public static void Hook() {
            listener ??= Net.Listen(ColdNet.StateId, OnReceiveState);
            Request();
        }

        public static void Request() {
            if (GameMain.Client != null && requestCooldown <= 0.0f) {
                requestCooldown = 0.5f;
                Net.SendEmpty(ColdNet.RequestId);
            }
        }

        public static void Update(float deltaTime) {
            if (requestCooldown > 0.0f) {
                requestCooldown -= deltaTime;
            }
        }

        private static void OnReceiveState(IReadMessage msg) {
            if (msg == null) { return; }
            byte mode = msg.ReadByte();
            int receivedHulls = 0;
            if (mode == 0) {
                ushort count = msg.ReadUInt16();
                receivedHulls = count;
                for (int i = 0; i < count; i++) {
                    ushort hullId = msg.ReadUInt16();
                    byte q = msg.ReadByte();
                    ColdField.ApplyNetworkHullTemperature(hullId, ColdNet.Dequantize(q));
                }
            } else if (mode == 1) {
                uint changed = NativeWire.ReadVarUInt32(msg);
                receivedHulls = (int)changed;
                for (int i = 0; i < changed; i++) {
                    ushort hullId = (ushort)NativeWire.ReadVarUInt32(msg);
                    byte q = msg.ReadByte();
                    ColdField.ApplyNetworkHullTemperature(hullId, ColdNet.Dequantize(q));
                }
            }
            LastReceivedAt = Timing.TotalTime;
            LastReceivedHulls = receivedHulls;
            ReceivedMessages++;
            Synced = true;
        }

        public static void Reset() {
            requestCooldown = 0.0f;
            LastReceivedAt = double.NegativeInfinity;
            LastReceivedHulls = 0;
            ReceivedMessages = 0;
            Synced = false;
            Request();
        }

        public static void Release() {
            listener?.Dispose();
            listener = null;
            requestCooldown = 0.0f;
            Reset();
        }
    }
}
