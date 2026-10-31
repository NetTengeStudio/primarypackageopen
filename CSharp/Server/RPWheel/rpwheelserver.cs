#nullable enable

using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.RPWheel
{
    public class RPWheelServer : PrimaryPackage.Core.IModule
    {
        public void RoundStart() { }

        public void Attach(Harmony harmony)
        {
            EmoteCatalog.Load();
            RpUtil.PatchNamespace(harmony);
            RegisterReceiver();
        }


        public void Release()
        {
        }

        private void RegisterReceiver()
        {
            PrimaryPackage.Core.ModNet.Receive(Cfg.NetId, HandleEmoteRequest, Cfg.EmoteCooldown);
        }

        private static void HandleEmoteRequest(IReadMessage msg, Client sender)
        {
            if ((NetHeader)msg.ReadRangedInteger(Cfg.HeaderMin, Cfg.HeaderMax) != NetHeader.EmoteRequest) return;
            int emoteId = msg.ReadByte();
            if (EmoteCatalog.Get(emoteId) == null) return;

            var ch = sender.Character;
            if (ch == null || ch.IsDead || ch.Removed) return;

            Broadcast(ch, emoteId);
        }

        private static void Broadcast(Character origin, int emoteId)
        {
            var clients = GameMain.Server?.ConnectedClients;
            if (clients == null) return;
            float rangeSq = Cfg.EmoteRange * Cfg.EmoteRange;
            foreach (var client in clients)
            {
                var listener = client.Character;
                if (listener != null && listener != origin &&
                    Vector2.DistanceSquared(listener.WorldPosition, origin.WorldPosition) > rangeSq)
                    continue;
                PrimaryPackage.Core.ModNet.Send(Cfg.NetId, client.Connection, outMsg =>
                {
                    outMsg.WriteRangedInteger((int)NetHeader.EmoteBroadcast, Cfg.HeaderMin, Cfg.HeaderMax);
                    outMsg.WriteUInt16(origin.ID);
                    outMsg.WriteByte((byte)emoteId);
                });
            }
        }

    }
}
