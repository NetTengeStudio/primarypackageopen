using System;
using System.Buffers;
using Barotrauma;
using Barotrauma.Networking;
using PrimaryPackage.Core;

namespace PrimaryPackage.WallCollision {
    internal readonly struct WallRuleRecord {
        internal readonly ushort Id;
        internal readonly WallMode Mode;
        internal readonly WallFace Face;

        internal WallRuleRecord(ushort id, WallMode mode, WallFace face) {
            Id = id;
            Mode = mode;
            Face = face;
        }
    }

    public static class WallCollisionClient {
        private const float FirstRequestDelay = 0.5f;
        private const float MaxRequestDelay = 15f;

        private static bool hooked;
        private static bool synced;
        private static float timer;
        private static float delay = FirstRequestDelay;
        private static object clientIdentity;
        private static object peerIdentity;
        private static object connectionIdentity;
        private static object sessionIdentity;

        public static bool Synced => synced;
        public static int ReceivedSnapshots { get; private set; }
        public static int DroppedStale { get; private set; }

        public static void Hook() {
            if (hooked) { return; }
            hooked = true;
            INet.Net.Receive(WallCollisionNet.StateId, OnState);
        }

        public static void OnRoundStart() {
            synced = false;
            timer = FirstRequestDelay;
            delay = FirstRequestDelay;
            clientIdentity = GameMain.Client;
            peerIdentity = GameMain.Client?.ClientPeer;
            connectionIdentity = GameMain.Client?.ClientPeer?.ServerConnection;
            sessionIdentity = GameMain.GameSession;
            WallCollisionNet.BeginEpoch(0);
        }

        public static void Update(float deltaTime) {
            object client = GameMain.Client;
            object peer = GameMain.Client?.ClientPeer;
            object connection = GameMain.Client?.ClientPeer?.ServerConnection;
            object session = GameMain.GameSession;
            if (!ReferenceEquals(client, clientIdentity) ||
                !ReferenceEquals(peer, peerIdentity) ||
                !ReferenceEquals(connection, connectionIdentity) ||
                !ReferenceEquals(session, sessionIdentity)) {
                clientIdentity = client;
                peerIdentity = peer;
                connectionIdentity = connection;
                sessionIdentity = session;
                synced = false;
                timer = FirstRequestDelay;
                delay = FirstRequestDelay;
                WallCollisionNet.Accept(0);
                WallRules.Reset();
            }
            if (synced || deltaTime <= 0f || connection is null || session is null) { return; }
            timer -= deltaTime;
            if (timer > 0f) { return; }
            if (INet.Net.Send(WallCollisionNet.RequestId, static message => message.WriteByte(0))) {
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
            try { ReadState(message); }
            catch (Exception) { Resync(); }
        }

        private static void ReadState(IReadMessage message) {
            WallPacket kind = (WallPacket)message.ReadByte();
            uint epoch = message.ReadUInt32();
            uint revision = message.ReadUInt32();
            if (kind == WallPacket.Snapshot) {
                bool ready = message.ReadByte() != 0;
                int count = message.ReadUInt16();
                if (epoch == 0 || count > WallCollisionNet.MaxRules || (!ready && count != 0)) { Resync(); return; }
                WallRuleRecord[] records = ArrayPool<WallRuleRecord>.Shared.Rent(count);
                try {
                    for (int i = 0; i < count; i++) {
                        records[i] = new WallRuleRecord(message.ReadUInt16(),
                            (WallMode)message.ReadByte(), (WallFace)message.ReadByte());
                    }
                    if (synced && epoch == WallCollisionNet.Epoch && !Newer(revision)) {
                        DroppedStale++;
                        return;
                    }
                    for (int i = 0; i < count; i++) {
                        WallRuleRecord record = records[i];
                        if ((record.Mode != WallMode.Solid && record.Mode != WallMode.OneWay) ||
                            (byte)record.Face > (byte)WallFace.MinusY ||
                            Entity.FindEntityByID(record.Id) is not Structure wall || wall.Removed) {
                            Resync();
                            return;
                        }
                    }
                    bool applied = true;
                    WallContacts.BeginBatch();
                    try {
                        WallRules.Reset();
                        WallRules.SetReady(ready);
                        for (int i = 0; i < count; i++) {
                            WallRuleRecord record = records[i];
                            if (WallRules.Apply(record.Id, record.Mode, record.Face)) { continue; }
                            applied = false;
                            WallRules.Reset();
                            break;
                        }
                    }
                    finally {
                        WallContacts.EndBatch();
                    }
                    if (!applied) { Resync(); return; }
                }
                finally {
                    ArrayPool<WallRuleRecord>.Shared.Return(records);
                }
                WallCollisionNet.BeginEpoch(epoch);
                WallCollisionNet.Accept(revision);
                synced = true;
                delay = FirstRequestDelay;
                ReceivedSnapshots++;
                return;
            }
            if (kind != WallPacket.Set || !synced || !WallRules.Ready) { return; }
            if (epoch != WallCollisionNet.Epoch) { Resync(); return; }
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
