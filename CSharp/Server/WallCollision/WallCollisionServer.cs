using Barotrauma.Networking;
using PrimaryPackage.Core;

namespace PrimaryPackage.WallCollision {
    public static class WallCollisionServer {
        private static bool hooked;

        public static void Hook() {
            if (hooked) { return; }
            hooked = true;
            ModNet.Receive(WallCollisionNet.RequestId, OnRequest, 1.0);
        }

        public static void OnRoundStart() {
            ushort revision = WallCollisionNet.Advance();
            ModNet.Broadcast(WallCollisionNet.StateId, message => WriteSnapshot(message, revision));
        }

        public static void Broadcast(in RuleChange change) {
            if (!change.Changed) { return; }
            ushort revision = WallCollisionNet.Advance();
            ushort id = change.Id;
            byte mode = (byte)change.Mode;
            byte face = (byte)change.Face;
            ModNet.Broadcast(WallCollisionNet.StateId, message => {
                message.WriteByte((byte)WallPacket.Set);
                message.WriteUInt16(revision);
                message.WriteUInt16(id);
                message.WriteByte(mode);
                message.WriteByte(face);
            });
        }

        private static void OnRequest(IReadMessage message, Client sender) {
            if (sender == null || sender.Connection == null) { return; }
            ushort revision = WallCollisionNet.Revision;
            ModNet.Send(WallCollisionNet.StateId, sender.Connection, snapshot => WriteSnapshot(snapshot, revision));
        }

        private static void WriteSnapshot(IWriteMessage message, ushort revision) {
            message.WriteByte((byte)WallPacket.Snapshot);
            message.WriteUInt16(revision);
            int count = WallRules.Count;
            message.WriteUInt16((ushort)count);
            for (int i = 0; i < count; i++) {
                WallRule rule = WallRules.At(i);
                message.WriteUInt16(rule.Id);
                message.WriteByte((byte)rule.Mode);
                message.WriteByte((byte)rule.Face);
            }
        }
    }
}
