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

        public static void OnRoundStart() => ResetSynchronization();

        public static void OnRoundEnd() => ResetSynchronization();

        private static void ResetSynchronization() {
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
            ushort revision = message.ReadUInt16();
            if (kind == WallPacket.Snapshot) {
                if (synced && !Newer(revision)) { DroppedStale++; return; }
                int count = message.ReadUInt16();
                WallRules.Reset();
                bool resolved = true;
                for (int i = 0; i < count; i++) {
                    ushort id = message.ReadUInt16();
                    WallMode mode = (WallMode)message.ReadByte();
                    WallFace face = (WallFace)message.ReadByte();
                    if (!WallRules.Apply(id, mode, face)) { resolved = false; }
                }
                // A fresh authoritative snapshot establishes the revision epoch.
                // After reconnecting, the new server may have a lower ushort revision.
                WallCollisionNet.Accept(revision);
                synced = resolved;
                ReceivedSnapshots++;
                return;
            }
            if (kind != WallPacket.Set || !synced) { return; }
            if (!Newer(revision)) { DroppedStale++; return; }
            if (revision != unchecked((ushort)(WallCollisionNet.Revision + 1))) {
                // A missing reliable delta would leave the client's rule table incomplete.
                // Request a full snapshot rather than applying an out-of-sequence update.
                ResetSynchronization();
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

        private static bool Newer(ushort revision) => (short)(revision - WallCollisionNet.Revision) > 0;

        public static string Status() {
            if (GameMain.Client == null) { return "локально (одиночная игра)"; }
            return (synced ? "получена" : "ожидается") + ", снимков " + ReceivedSnapshots +
                   (DroppedStale > 0 ? ", отброшено устаревших " + DroppedStale : string.Empty);
        }
    }
}
