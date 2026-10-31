using System;
using System.Collections.Generic;
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
        private static int graphVersion = -1;
        private static readonly HashSet<ushort> receivedFullHulls = new HashSet<ushort>();

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
            graphVersion = -1;
            receivedFullHulls.Clear();
            LastReceivedAt = double.NegativeInfinity;
            LastReceivedHulls = 0;
            ReceivedMessages = 0;
        }

        private const float MaxRequestInterval = 30.0f;

        public static void Update(float deltaTime) {
            if (GameMain.Client == null || GameMain.GameSession == null) { return; }
            ColdField.Touch();
            if (graphVersion != ColdField.Version) {
                InvalidateGraph();
            }
            if (ColdField.Count == 0 || synced) { return; }
            requestTimer -= deltaTime;
            if (requestTimer > 0.0f) { return; }
            requestTimer = backoff;
            if (backoff < MaxRequestInterval) { backoff *= 2.0f; }
            PrimaryPackage.Core.ModNet.Send(ColdNet.RequestId, null);
        }

        private static void InvalidateGraph() {
            graphVersion = ColdField.Version;
            receivedFullHulls.Clear();
            synced = false;
            requestTimer = 0.0f;
            backoff = RequestInterval;
        }

        private static void OnState(IReadMessage msg) {
            byte flags = msg.ReadByte();
            int count = msg.ReadUInt16();
            if (count > ColdNet.MaxPerMessage) { return; }
            ColdField.Touch();
            if (graphVersion != ColdField.Version) { InvalidateGraph(); }
            bool full = (flags & ColdNet.FlagFull) != 0;
            int applied = 0;
            for (int i = 0; i < count; i++) {
                ushort id = msg.ReadUInt16();
                float value = ColdNet.Dequantize(msg.ReadByte());
                if (Entity.FindEntityByID(id) is not Hull hull) { continue; }
                if (ColdField.IndexOf(hull) < 0) { continue; }
                ColdField.Set(hull, value);
                applied++;
                if (full) { receivedFullHulls.Add(id); }
            }
            // New servers send an explicit terminator; legacy servers only
            // send data chunks. In either case synchronize only when every
            // currently indexed local hull has been observed in full state.
            if (full && receivedFullHulls.Count >= ColdField.Count &&
                (count == 0 || receivedFullHulls.Count > 0))
            {
                synced = true;
            }
            LastReceivedAt = Timing.TotalTime;
            LastReceivedHulls = applied;
            ReceivedMessages++;
        }
    }
}
