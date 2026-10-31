#nullable enable

using System;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;

namespace PrimaryPackage.RPWheel
{
    internal static class RPWheelNet
    {
        internal static bool IsMultiplayer => GameMain.Client != null;

        internal static void Register()
        {
            PrimaryPackage.Core.ModNet.Receive(Cfg.NetId, Handle);
        }

        private static void Handle(IReadMessage msg)
        {
            if ((NetHeader)msg.ReadRangedInteger(Cfg.HeaderMin, Cfg.HeaderMax) != NetHeader.EmoteBroadcast) return;

            ushort id      = msg.ReadUInt16();
            int    emoteId = msg.ReadByte();
            if (EmoteCatalog.Get(emoteId) == null || Entity.FindEntityByID(id) is not Character ch) return;

            RPWheelMod.EmoteDisplay?.ShowById(ch, emoteId);
            EmoteAudio.Play(emoteId, ch.WorldPosition);
        }
    }

    internal static class EmoteNet
    {
        private static double _lastSendTime = double.NegativeInfinity;

        internal static void Send(byte emoteId)
        {
            double now = Timing.TotalTime;
            if (now - _lastSendTime < Cfg.EmoteCooldown) return;

            var ch = Character.Controlled;
            if (ch == null || EmoteCatalog.Get(emoteId) == null) return;
            _lastSendTime = now;

            if (!RPWheelNet.IsMultiplayer)
            {
                RPWheelMod.EmoteDisplay?.ShowById(ch, emoteId);
                EmoteAudio.Play(emoteId, ch.WorldPosition);
                return;
            }

            PrimaryPackage.Core.ModNet.Send(Cfg.NetId, msg => {
                msg.WriteRangedInteger((int)NetHeader.EmoteRequest, Cfg.HeaderMin, Cfg.HeaderMax);
                msg.WriteByte(emoteId);
            });
        }
    }
}
