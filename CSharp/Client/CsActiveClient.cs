using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using HarmonyLib;

namespace PrimaryPackage.Netcode {
    public sealed class CsActiveClientPlugin : PrimaryPackage.Core.IModule, IEventUpdate, IEventServerConnected {
        private static readonly double[] Schedule = { 0.25, 1.0, 3.0, 7.0, 15.0, 30.0, 55.0, 85.0 };
        private const double HeartbeatSeconds = 20.0;

        private GameClient tracked;
        private double elapsed;
        private double nextHeartbeat;
        private int sent;
        private bool acknowledged;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            PrimaryPackage.Core.ModNet.Receive(CsProtocol.NetId, OnAck);
            LuaCsSetup.Instance?.EventService?.Subscribe<IEventUpdate>(this);
            LuaCsSetup.Instance?.EventService?.Subscribe<IEventServerConnected>(this);
        }

        public void OnServerConnected() => Arm(GameMain.Client);

        private void OnAck(IReadMessage msg) {
            if (msg.BitPosition + 8 > msg.LengthBits) { return; }
            byte kind = msg.ReadByte();
            if (kind == CsProtocol.Ack) { acknowledged = true; return; }
            if (kind == CsProtocol.Probe) { Announce(); }
        }

        public void OnUpdate(double fixedDeltaTime) {
            GameClient client = GameMain.Client;
            if (!ReferenceEquals(client, tracked)) { Arm(client); }
            if (client == null || acknowledged || client.ClientPeer == null) { return; }

            elapsed += fixedDeltaTime;

            if (sent < Schedule.Length) {
                if (elapsed < Schedule[sent]) { return; }
                if (!Announce()) { return; }
                sent++;
                nextHeartbeat = elapsed + HeartbeatSeconds;
                return;
            }

            if (elapsed < nextHeartbeat) { return; }
            nextHeartbeat = elapsed + HeartbeatSeconds;
            Announce();
        }

        private static bool Announce() =>
            PrimaryPackage.Core.ModNet.Send(CsProtocol.NetId, static msg => msg.WriteByte(CsProtocol.Proto));

        private void Arm(GameClient client) {
            tracked = client;
            elapsed = 0.0;
            nextHeartbeat = 0.0;
            sent = 0;
            acknowledged = false;
        }

        public void Release() {
            LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventUpdate>(this);
            LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventServerConnected>(this);
            tracked = null;
            acknowledged = false;
        }
    }
}
