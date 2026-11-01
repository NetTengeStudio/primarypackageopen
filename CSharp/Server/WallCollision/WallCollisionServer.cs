using Barotrauma.Networking;
using System.Security.Cryptography;
using System;
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
            WallCollisionNet.BeginEpoch((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue));
            uint revision = WallCollisionNet.Advance();
            ModNet.Broadcast(WallCollisionNet.StateId, message => WriteSnapshot(message, revision));
        }

        public static int Activate() {
            if (WallCollisionNet.Epoch == 0) { WallCollisionNet.BeginEpoch((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue)); }
            int count = WallRules.ActivatePrepared();
            uint revision = WallCollisionNet.Advance();
            ModNet.Broadcast(WallCollisionNet.StateId, message => WriteSnapshot(message, revision));
            return count;
        }

        public static void Broadcast(in RuleChange change) {
            if (!change.Changed || !WallRules.Ready) { return; }
            uint revision = WallCollisionNet.Advance();
            ushort id = change.Id;
            byte mode = (byte)change.Mode;
            byte face = (byte)change.Face;
            ModNet.Broadcast(WallCollisionNet.StateId, message => {
                message.WriteByte((byte)WallPacket.Set);
                message.WriteUInt32(WallCollisionNet.Epoch);
                message.WriteUInt32(revision);
                message.WriteUInt16(id);
                message.WriteByte(mode);
                message.WriteByte(face);
            });
        }

        private static void OnRequest(IReadMessage message, Client sender) {
            if (sender == null || sender.Connection == null) { return; }
            uint revision = WallCollisionNet.Revision;
            ModNet.Send(WallCollisionNet.StateId, sender.Connection, snapshot => WriteSnapshot(snapshot, revision));
        }

        private static void WriteSnapshot(IWriteMessage message, uint revision) {
            message.WriteByte((byte)WallPacket.Snapshot);
            message.WriteUInt32(WallCollisionNet.Epoch);
            message.WriteUInt32(revision);
            message.WriteByte(WallRules.Ready ? (byte)1 : (byte)0);
            int count = WallRules.Ready ? WallRules.Count : 0;
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
