using System;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;

namespace PrimaryPackage.Cold {
    public static class ColdClientNet {
        private const float RequestInterval = 3.0f;

        private static bool initialized;
        private static bool synced;
        private static float requestTimer;
        private static float backoff = RequestInterval;

        public static bool Synced => synced;
        public static double LastReceivedAt { get; private set; } = double.NegativeInfinity;
        public static int LastReceivedHulls { get; private set; }
        public static int ReceivedMessages { get; private set; }

        public static void Hook() {
            if (initialized) { return; }
            initialized = true;
            PrimaryPackage.Core.ModNet.Receive(ColdNet.StateId, OnState);
        }

        public static void Reset() {
            synced = false;
            requestTimer = 0.0f;
            backoff = RequestInterval;
            LastReceivedAt = double.NegativeInfinity;
            LastReceivedHulls = 0;
            ReceivedMessages = 0;
        }

        private const float MaxRequestInterval = 30.0f;

        public static void Update(float deltaTime) {
            if (GameMain.Client == null || GameMain.GameSession == null || synced) { return; }
            ColdField.Touch();
            if (ColdField.Count == 0) { return; }
            requestTimer -= deltaTime;
            if (requestTimer > 0.0f) { return; }
            requestTimer = backoff;
            if (backoff < MaxRequestInterval) { backoff *= 2.0f; }
            PrimaryPackage.Core.ModNet.Send(ColdNet.RequestId, null);
        }

        private static void OnState(IReadMessage msg) {
            byte flags = msg.ReadByte();
            int count = msg.ReadUInt16();
            if (count > ColdNet.MaxPerMessage) { return; }
            ColdField.Touch();
            int applied = 0;
            for (int i = 0; i < count; i++) {
                ushort id = msg.ReadUInt16();
                float value = ColdNet.Dequantize(msg.ReadByte());
                if (Entity.FindEntityByID(id) is not Hull hull) { continue; }
                if (ColdField.IndexOf(hull) < 0) { continue; }
                ColdField.Set(hull, value);
                applied++;
            }
            // Data chunks alone are not a completed snapshot: the server sends
            // a final reliable marker only after writing every chunk.
            if ((flags & (ColdNet.FlagFull | ColdNet.FlagSnapshotComplete)) ==
                (ColdNet.FlagFull | ColdNet.FlagSnapshotComplete) && count == 0)
            {
                synced = true;
            }
            LastReceivedAt = Timing.TotalTime;
            LastReceivedHulls = applied;
            ReceivedMessages++;
        }
    }
}
