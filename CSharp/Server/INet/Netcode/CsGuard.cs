using System.Collections.Generic;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using HarmonyLib;

namespace INet {
    public sealed class CsGuardPlugin : PrimaryPackage.Core.IModule, IEventUpdate, IEventClientConnected, IEventClientDisconnected {
        private const double CheckInterval = 1.0;

        private struct Watch {
            public double Since;
            public double ProbedAt;
        }

        private readonly Dictionary<Client, Watch> pending = new();
        private readonly HashSet<Client> verified = new();
        private readonly List<Client> kickBuffer = new();
        private readonly List<Client> probeBuffer = new();
        private readonly List<Client> reanchorBuffer = new();
        private readonly List<Client> dropBuffer = new();
        private double nextCheck;

        public static long Verifications;
        public static long Probes;
        public static long Kicks;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            INet.Net.Receive(CsProtocol.NetId, OnAlive);
            LuaCsSetup.Instance?.EventService?.Subscribe<IEventClientConnected>(this);
            LuaCsSetup.Instance?.EventService?.Subscribe<IEventClientDisconnected>(this);
            LuaCsSetup.Instance?.EventService?.Subscribe<IEventUpdate>(this);
        }

        private void OnAlive(IReadMessage msg, Client client) {
            if (client == null || msg.BitPosition + 8 > msg.LengthBits || msg.ReadByte() != CsProtocol.Proto) { return; }
            pending.Remove(client);
            if (verified.Add(client)) { Verifications++; }
            INet.Net.Send(CsProtocol.NetId, client.Connection,
                static reply => reply.WriteByte(CsProtocol.Ack));
        }

        public void OnClientConnected(Client client) {
            if (client == null || verified.Contains(client) || IsExempt(client)) { return; }
            pending[client] = new Watch { Since = Timing.TotalTime, ProbedAt = double.NaN };
        }

        public void OnClientDisconnected(Client client) {
            if (client == null) { return; }
            pending.Remove(client);
            verified.Remove(client);
        }

        public void OnUpdate(double fixedDeltaTime) {
            double now = Timing.TotalTime;
            if (now < nextCheck || pending.Count == 0) { return; }
            nextCheck = now + CheckInterval;

            kickBuffer.Clear();
            probeBuffer.Clear();
            reanchorBuffer.Clear();
            dropBuffer.Clear();

            foreach (KeyValuePair<Client, Watch> kv in pending) {
                Client c = kv.Key;
                Watch watch = kv.Value;
                switch (CsGuardPolicy.Decide(now, watch.Since, watch.ProbedAt, IsExempt(c), IsSettling(c))) {
                    case CsGuardVerdict.Drop: dropBuffer.Add(c); break;
                    case CsGuardVerdict.Reanchor: reanchorBuffer.Add(c); break;
                    case CsGuardVerdict.Probe: probeBuffer.Add(c); break;
                    case CsGuardVerdict.Kick: kickBuffer.Add(c); break;
                }
            }

            for (int i = 0; i < dropBuffer.Count; i++) { pending.Remove(dropBuffer[i]); }

            for (int i = 0; i < reanchorBuffer.Count; i++) {
                pending[reanchorBuffer[i]] = new Watch { Since = now, ProbedAt = double.NaN };
            }

            for (int i = 0; i < probeBuffer.Count; i++) {
                Client c = probeBuffer[i];
                bool asked = INet.Net.Send(CsProtocol.NetId, c.Connection,
                    static probe => probe.WriteByte(CsProtocol.Probe));
                if (!asked) {
                    pending[c] = new Watch { Since = now, ProbedAt = double.NaN };
                    continue;
                }
                Probes++;
                pending[c] = new Watch { Since = pending[c].Since, ProbedAt = now };
            }

            for (int i = 0; i < kickBuffer.Count; i++) {
                Client c = kickBuffer[i];
                pending.Remove(c);
                Kicks++;
                GameMain.Server.KickClient(c, TextManager.Get("pp_csguard.kick").Value);
            }
        }

        private static bool IsExempt(Client client) {
            GameServer server = GameMain.Server;
            if (server == null || client.Connection == null) { return true; }
            return server.OwnerConnection != null && client.Connection == server.OwnerConnection;
        }

        private static bool IsSettling(Client client) {
            GameServer server = GameMain.Server;
            if (server == null) { return true; }
            if (client.NeedsMidRoundSync || client.Connection.Status != NetworkConnectionStatus.Connected) { return true; }
            if (server.GameStarted && !client.InGame) { return true; }
            IReadOnlyList<FileSender.FileTransferOut> transfers = server.FileSender.ActiveTransfers;
            for (int i = 0; i < transfers.Count; i++) {
                if (transfers[i].Connection == client.Connection) { return true; }
            }
            return false;
        }

        public void Release() {
            LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventClientConnected>(this);
            LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventClientDisconnected>(this);
            LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventUpdate>(this);
            pending.Clear();
            verified.Clear();
            kickBuffer.Clear();
            probeBuffer.Clear();
            reanchorBuffer.Clear();
            dropBuffer.Clear();
        }
    }
}
