using System;
using Barotrauma;
using Barotrauma.Networking;
using PrimaryPackage.Core;

namespace PrimaryPackage.WallCollision {
    public static class WallCollisionClient {
        private const float FirstRequestDelay = 0.5f;
        private const float MaxRequestDelay = 15f;
        private const int MaxRules = 8192;

        private static bool hooked;
        private static bool synced;
        private static float timer;
        private static float delay = FirstRequestDelay;
        private static object connectionIdentity;
        private static object sessionIdentity;

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
            connectionIdentity = GameMain.Client;
            sessionIdentity = GameMain.GameSession;
            WallCollisionNet.Accept(0);
        }

        public static void Update(float deltaTime) {
            object client = GameMain.Client;
            object session = GameMain.GameSession;
            if (!ReferenceEquals(client, connectionIdentity) || !ReferenceEquals(session, sessionIdentity)) {
                connectionIdentity = client;
                sessionIdentity = session;
                synced = false;
                timer = FirstRequestDelay;
                delay = FirstRequestDelay;
                WallCollisionNet.Accept(0);
                WallRules.Reset();
            }
            if (synced || deltaTime <= 0f || client is null || session is null) { return; }
            timer -= deltaTime;
            if (timer > 0f) { return; }
            if (ModNet.Send(WallCollisionNet.RequestId, static message => message.WriteByte(0))) {
                timer = delay;
                delay = Math.Min(MaxRequestDelay, delay * 2f);
            }
            else {
                timer = FirstRequestDelay;
            }
        }

        private static void Resync() {
            synced = false;
            WallRules.Reset();
            timer = 0f;
            delay = FirstRequestDelay;
        }

        private static void OnState(IReadMessage message) {
            WallPacket kind = (WallPacket)message.ReadByte();
            uint revision = message.ReadUInt32();
            if (kind == WallPacket.Snapshot) {
                bool ready = message.ReadByte() != 0;
                int count = message.ReadUInt16();
                if (count > MaxRules || (!ready && count != 0)) { Resync(); return; }
                ushort[] ids = new ushort[count];
                WallMode[] modes = new WallMode[count];
                WallFace[] faces = new WallFace[count];
                for (int i = 0; i < count; i++) {
                    ids[i] = message.ReadUInt16();
                    modes[i] = (WallMode)message.ReadByte();
                    faces[i] = (WallFace)message.ReadByte();
                }
                if (synced && !Newer(revision)) { DroppedStale++; return; }
                for (int i = 0; i < count; i++) {
                    if ((modes[i] != WallMode.Solid && modes[i] != WallMode.OneWay) ||
                        (byte)faces[i] > (byte)WallFace.MinusY ||
                        Entity.FindEntityByID(ids[i]) is not Structure wall || wall.Removed) {
                        Resync();
                        return;
                    }
                }
                WallRules.Reset();
                WallRules.SetReady(ready);
                for (int i = 0; i < count; i++) {
                    if (!WallRules.Apply(ids[i], modes[i], faces[i])) {
                        Resync();
                        return;
                    }
                }
                WallCollisionNet.Accept(revision);
                synced = true;
                delay = FirstRequestDelay;
                ReceivedSnapshots++;
                return;
            }
            if (kind != WallPacket.Set || !synced || !WallRules.Ready) { return; }
            if (!Newer(revision)) { DroppedStale++; return; }
            if (unchecked(revision - WallCollisionNet.Revision) != 1u) {
                Resync();
                return;
            }
            ushort changedId = message.ReadUInt16();
            WallMode changedMode = (WallMode)message.ReadByte();
            WallFace changedFace = (WallFace)message.ReadByte();
            if (!WallRules.Apply(changedId, changedMode, changedFace)) {
                Resync();
                return;
            }
            WallCollisionNet.Accept(revision);
        }

        private static bool Newer(uint revision)
            => unchecked((int)(revision - WallCollisionNet.Revision)) > 0;

        public static string Status() {
            if (GameMain.Client == null) { return "локально (одиночная игра)"; }
            return (synced ? (WallRules.Ready ? "активна" : "ожидает ec_ready") : "ожидается") +
                ", снимков " + ReceivedSnapshots +
                (DroppedStale > 0 ? ", отброшено устаревших " + DroppedStale : string.Empty);
        }
    }
}
