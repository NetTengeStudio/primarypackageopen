using System;
using Barotrauma.Networking;
using PrimaryPackage.Core;

namespace PrimaryPackage.WallCollision {
    public static class WallCollisionClient {
        private const float FirstRequestDelay = 1.0f;
        private const float MaxRequestDelay = 30.0f;

        private static bool hooked;
        private static bool synced;
        private static float timer;
        private static float delay = FirstRequestDelay;

        public static bool Synced => synced;

        public static int ReceivedSnapshots { get; private set; }

        public static int DroppedStale { get; private set; }

        public static void Hook() {
            if (hooked) { return; }
            hooked = true;
            ModNet.Receive(WallCollisionNet.StateId, OnState);
        }

        public static void OnRoundStart() {
            synced = false;
            timer = FirstRequestDelay;
            delay = FirstRequestDelay;
        }

        public static void Update(float deltaTime) {
            if (synced || deltaTime <= 0f) { return; }
            if (GameMain.Client == null || GameMain.GameSession == null) { return; }
            timer -= deltaTime;
            if (timer > 0f) { return; }
            timer = delay;
            if (delay < MaxRequestDelay) { delay *= 2f; }
            ModNet.Send(WallCollisionNet.RequestId, static message => message.WriteByte(0));
        }

        private static void OnState(IReadMessage message) {
            WallPacket kind = (WallPacket)message.ReadByte();
            uint revision = message.ReadUInt32();
            if (kind == WallPacket.Snapshot) {
                bool ready = message.ReadByte() != 0;
                if (synced && !Newer(revision)) { DroppedStale++; return; }
                int count = message.ReadUInt16();
                WallRules.Reset();
                WallRules.SetReady(ready);
                bool resolved = true;
                for (int i = 0; i < count; i++) {
                    ushort id = message.ReadUInt16();
                    WallMode mode = (WallMode)message.ReadByte();
                    WallFace face = (WallFace)message.ReadByte();
                    if (!WallRules.Apply(id, mode, face)) { resolved = false; }
                }
                WallCollisionNet.Accept(revision);
                synced = resolved;
                if (!resolved)
                {
                    // A joining client may not have created every Structure yet.
                    // Never run an incomplete wall snapshot; retry after world load.
                    WallRules.Reset();
                    timer = FirstRequestDelay;
                    delay = FirstRequestDelay;
                }
                ReceivedSnapshots++;
                return;
            }
            if (!synced || !WallRules.Ready || kind != WallPacket.Set) { return; }
            if (!Newer(revision)) { DroppedStale++; return; }
            if (unchecked(revision - WallCollisionNet.Revision) != 1u) {
                synced = false;
                timer = 0f;
                delay = FirstRequestDelay;
                return;
            }
            ushort changedId = message.ReadUInt16();
            WallMode changedMode = (WallMode)message.ReadByte();
            WallFace changedFace = (WallFace)message.ReadByte();
            if (!WallRules.Apply(changedId, changedMode, changedFace)) {
                synced = false;
                timer = FirstRequestDelay;
                delay = FirstRequestDelay;
                return;
            }
            WallCollisionNet.Accept(revision);
        }

        private static bool Newer(uint revision) => unchecked((int)(revision - WallCollisionNet.Revision)) > 0;

        public static string Status() {
            if (GameMain.Client == null) { return "локально (одиночная игра)"; }
            return (synced ? (WallRules.Ready ? "активна" : "ожидает ec_ready") : "ожидается") + ", снимков " + ReceivedSnapshots +
                   (DroppedStale > 0 ? ", отброшено устаревших " + DroppedStale : string.Empty);
        }
    }
}
