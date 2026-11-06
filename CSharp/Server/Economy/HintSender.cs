using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.Economy {
    internal static class HintSender {
        public static void Info(Client c, string text) => SendTo(c, text, HintToastNet.SeverityInfo);
        public static void Success(Client c, string text) => SendTo(c, text, HintToastNet.SeveritySuccess);
        public static void Error(Client c, string text) => SendTo(c, text, HintToastNet.SeverityError);
        public static void SendTo(Client c, string text, byte severity) {
            if (string.IsNullOrWhiteSpace(text)) { return; }
            if (c == null) {
                DebugConsole.NewMessage(text, severity == HintToastNet.SeverityError ? Color.OrangeRed : Color.LightGreen);
                return;
            }
            INet.Net.Send(HintToastNet.NetId, c.Connection, msg => {
                msg.WriteByte(severity);
                msg.WriteString(text);
            });
        }
        public static void SendToCharacter(Character character, string text, byte severity) {
            Client c = FindClient(character);
            if (c != null) { SendTo(c, text, severity); }
        }
        public static void SendAll(string text, byte severity) {
            if (GameMain.Server == null || string.IsNullOrWhiteSpace(text)) { return; }
            foreach (Client c in GameMain.Server.ConnectedClients) { SendTo(c, text, severity); }
        }
        public static void SendNear(Vector2 worldPos, float range, string text, byte severity) {
            if (GameMain.Server == null) { return; }
            float rangeSqr = range * range;
            foreach (Client c in GameMain.Server.ConnectedClients) {
                Character ch = c.Character;
                if (ch == null || ch.Removed || ch.IsDead) { continue; }
                if (Vector2.DistanceSquared(ch.WorldPosition, worldPos) > rangeSqr) { continue; }
                SendTo(c, text, severity);
            }
        }
        private static Client FindClient(Character character) {
            if (character == null || GameMain.Server == null) { return null; }
            foreach (Client c in GameMain.Server.ConnectedClients) {
                if (c.Character == character) { return c; }
            }
            return null;
        }
    }
}
