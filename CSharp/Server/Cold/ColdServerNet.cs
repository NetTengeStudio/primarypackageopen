using System;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using PrimaryPackage.Cold;
using HarmonyLib;

namespace PrimaryPackage.Cold {
    public static class ColdServerNet {
        private static byte[] sent = Array.Empty<byte>();
        private static ushort[] pending = Array.Empty<ushort>();
        private static byte[] pendingValues = Array.Empty<byte>();
        private static float keepAliveTimer;
        private static float netTimer;
        private static int knownVersion = -1;
        private static bool initialized;
        private static float lastQuantizeMin = float.NaN, lastQuantizeMax = float.NaN;

        public static double LastSentAt { get; private set; } = double.NegativeInfinity;
        public static int LastSentHulls { get; private set; }
        public static int SentMessages { get; private set; }
        public static bool LastTickSilent { get; private set; } = true;
        public static long InspectedHulls { get; private set; }

        public static void Reset() {
            sent = Array.Empty<byte>();
            knownVersion = -1;
            lastQuantizeMin = lastQuantizeMax = float.NaN;
            keepAliveTimer = 0.0f;
            netTimer = 0.0f;
            LastSentAt = double.NegativeInfinity;
            LastSentHulls = 0;
            SentMessages = 0;
            LastTickSilent = true;
            InspectedHulls = 0;
        }

        public static void Hook() {
            if (initialized) { return; }
            initialized = true;
            PrimaryPackage.Core.ModNet.Receive(ColdNet.RequestId, OnRequest, 2.0);
        }

        private static void OnRequest(IReadMessage msg, Client sender) {
            if (sender?.Connection == null) { return; }
            SendFull(sender.Connection);
        }

        public static void Broadcast(float step) {
            if (GameMain.Server == null) { return; }
            int count = ColdField.Count;
            if (count == 0) { return; }

            netTimer += step;
            if (netTimer < ColdConfig.NetInterval) { return; }
            float elapsed = netTimer;
            netTimer = 0.0f;

            bool forceAll = knownVersion != ColdField.Version ||
                lastQuantizeMin != ColdConfig.MinTemperature || lastQuantizeMax != ColdConfig.MaxTemperature;
            if (forceAll) {
                knownVersion = ColdField.Version;
                lastQuantizeMin = ColdConfig.MinTemperature;
                lastQuantizeMax = ColdConfig.MaxTemperature;
            }
            if (sent.Length < count) {
                sent = new byte[count];
                forceAll = true;
            }
            if (pending.Length < count) {
                pending = new ushort[count];
                pendingValues = new byte[count];
            }

            keepAliveTimer += elapsed;
            bool keepAlive = ColdConfig.NetKeepAlive > 0.0f && keepAliveTimer >= ColdConfig.NetKeepAlive;
            if (keepAlive) { keepAliveTimer = 0.0f; }

            int changed = 0;
            bool scanAll = forceAll || keepAlive;
            int toInspect = scanAll ? count : ColdField.ChangedCount;
            InspectedHulls += toInspect;
            for (int candidate = 0; candidate < toInspect; candidate++) {
                int i = scanAll ? candidate : ColdField.ChangedAt(candidate);
                byte q = ColdNet.Quantize(ColdField.Raw(i));
                if (q == sent[i] && !scanAll) { continue; }
                sent[i] = q;
                Hull hull = ColdField.HullAt(i);
                if (hull == null) { continue; }
                pending[changed] = hull.ID;
                pendingValues[changed] = q;
                changed++;
            }
            ColdField.ConsumeChanges();
            LastTickSilent = changed == 0;
            if (changed == 0) { return; }
            LastSentAt = Timing.TotalTime;
            LastSentHulls = changed;

            int offset = 0;
            while (offset < changed) {
                int chunk = Math.Min(ColdNet.MaxPerMessage, changed - offset);
                int start = offset;
                int size = chunk;
                PrimaryPackage.Core.ModNet.Broadcast(ColdNet.StateId, msg => {
                    msg.WriteByte(0);
                    msg.WriteUInt16((ushort)size);
                    for (int i = 0; i < size; i++) {
                        msg.WriteUInt16(pending[start + i]);
                        msg.WriteByte(pendingValues[start + i]);
                    }
                });
                SentMessages++;
                offset += chunk;
            }
        }

        private static void SendFull(NetworkConnection connection) {
            int count = ColdField.Count;
            int offset = 0;
            while (offset < count) {
                int chunk = Math.Min(ColdNet.MaxPerMessage, count - offset);
                int start = offset;
                int size = chunk;
                PrimaryPackage.Core.ModNet.Send(ColdNet.StateId, connection, msg => {
                    msg.WriteByte(ColdNet.FlagFull);
                    msg.WriteUInt16((ushort)size);
                    for (int i = 0; i < size; i++) {
                        Hull hull = ColdField.HullAt(start + i);
                        msg.WriteUInt16(hull == null ? (ushort)0 : hull.ID);
                        msg.WriteByte(ColdNet.Quantize(ColdField.Raw(start + i)));
                    }
                });
                offset += chunk;
            }
        }
    }

    public sealed class ColdServerPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) => ColdServerNet.Hook();



        public void Release() {}
    }
}
