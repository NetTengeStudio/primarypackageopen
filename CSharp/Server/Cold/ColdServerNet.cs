using System;
using Barotrauma;
using Barotrauma.Networking;
using INet;

namespace PrimaryPackage.Cold {
    public static class ColdServerNet {
        private static IDisposable listener;
        private static float syncTimer;
        private static float fullSyncTimer;
        private static byte[] lastSent = Array.Empty<byte>();

        public static bool LastTickSilent { get; private set; }
        public static long SentMessages { get; private set; }
        public static double LastSentAt { get; private set; }
        public static int LastSentHulls { get; private set; }

        public static void Hook() {
            listener ??= Net.Listen(ColdNet.RequestId, OnReceiveRequest, minInterval: 0.25);
        }

        public static void Update(float deltaTime) {
            if (GameMain.Server == null) { return; }
            syncTimer += deltaTime;
            fullSyncTimer += deltaTime;
            if (syncTimer >= ColdConfig.NetInterval) {
                syncTimer = 0.0f;
                Broadcast();
            }
        }

        private static void OnReceiveRequest(IReadMessage msg, Client sender) {
            if (sender?.Connection == null || GameMain.Server == null) { return; }
            int count = ColdField.Count;
            if (count == 0) { return; }
            Net.Send(ColdNet.StateId, sender.Connection, msgOut => WriteFullPayload(msgOut, count), DeliveryMethod.Reliable);
        }

        public static void Broadcast(float step = 0.0f) {
            int count = ColdField.Count;
            if (count == 0 || GameMain.Server == null) { return; }
            if (lastSent.Length < count) {
                Array.Resize(ref lastSent, count);
            }

            bool forceFull = fullSyncTimer >= 5.0f;
            if (forceFull) {
                fullSyncTimer = 0.0f;
                Net.Broadcast(ColdNet.StateId, msgOut => WriteFullPayload(msgOut, count), DeliveryMethod.Unreliable, NetAudience.InGame);
                return;
            }

            int changed = 0;
            for (int i = 0; i < count; i++) {
                byte q = ColdNet.Quantize(ColdField.Raw(i));
                if (q != lastSent[i]) { changed++; }
            }

            if (changed == 0) {
                LastTickSilent = true;
                return;
            }

            LastTickSilent = false;
            SentMessages++;
            LastSentAt = Timing.TotalTime;
            LastSentHulls = count;

            if (changed > count / 2) {
                fullSyncTimer = 0.0f;
                Net.Broadcast(ColdNet.StateId, msgOut => WriteFullPayload(msgOut, count), DeliveryMethod.Unreliable, NetAudience.InGame);
            } else {
                Net.Broadcast(ColdNet.StateId, msgOut => WriteDeltaPayload(msgOut, count, changed), DeliveryMethod.Unreliable, NetAudience.InGame);
            }
        }

        private static void WriteFullPayload(IWriteMessage msg, int count) {
            msg.WriteByte(0);
            msg.WriteUInt16((ushort)count);
            for (int i = 0; i < count; i++) {
                Hull h = ColdField.HullAt(i);
                byte q = ColdNet.Quantize(ColdField.Raw(i));
                if (i < lastSent.Length) { lastSent[i] = q; }
                msg.WriteUInt16(h?.ID ?? 0);
                msg.WriteByte(q);
            }
        }

        private static void WriteDeltaPayload(IWriteMessage msg, int count, int changed) {
            msg.WriteByte(1);
            NativeWire.WriteVarUInt32(msg, (uint)changed);
            for (int i = 0; i < count; i++) {
                byte q = ColdNet.Quantize(ColdField.Raw(i));
                if (q != lastSent[i]) {
                    lastSent[i] = q;
                    Hull h = ColdField.HullAt(i);
                    NativeWire.WriteVarUInt32(msg, h?.ID ?? 0);
                    msg.WriteByte(q);
                }
            }
        }

        public static void Reset() {
            syncTimer = 0.0f;
            fullSyncTimer = 0.0f;
            Array.Clear(lastSent, 0, lastSent.Length);
        }

        public static void Release() {
            listener?.Dispose();
            listener = null;
            syncTimer = 0.0f;
            fullSyncTimer = 0.0f;
        }
    }
}