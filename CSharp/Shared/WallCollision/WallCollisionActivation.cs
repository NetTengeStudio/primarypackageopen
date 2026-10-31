using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.WallCollision {
    public static class WallCollisionActivation {
        public static void Register() {
            DebugConsole.Command command = ModCommands.Register(
                "ec_ready",
                "ec_ready — server-authorized activation of walls marked by ec_init in the submarine editor.",
                OnLocalExecute,
                localOnClient: false,
                relayToServer: true);
#if SERVER
            if (command != null) { command.OnClientRequestExecute = OnClientRequest; }
#endif
        }

#if SERVER
        private static void OnClientRequest(Client client, Vector2 cursorWorld, string[] args) {
            if (client == null) { return; }
            if (!PrimaryPackage.MpPermissionGuard.CheckOrWarn(client, ClientPermissions.ConsoleCommands, "ec_ready")) {
                GameMain.Server?.SendConsoleMessage("ec_ready: недостаточно полномочий.", client, Color.Red);
                return;
            }
            Activate(client);
        }
#endif

        private static void OnLocalExecute(string[] args) {
#if CLIENT
            if (GameMain.Client != null) {
                DebugConsole.NewMessage("ec_ready: активацию выполняет сервер.", Color.Yellow);
                return;
            }
#endif
            Activate(null);
        }

        private static void Activate(Client sender) {
            if (GameMain.GameSession == null || GameMain.World == null) {
                Respond(sender, "ec_ready: сначала загрузите уровень.", Color.Yellow);
                return;
            }
            if (WallRules.Ready) {
                Respond(sender, "ec_ready: система уже активирована.", Color.Yellow);
                return;
            }
            int count;
#if SERVER
            count = WallCollisionServer.Activate();
#else
            count = WallRules.ActivatePrepared();
#endif
            Respond(sender, "ec_ready: активировано стен " + count + ".",
                count > 0 ? Color.LightGreen : Color.Yellow);
        }

        private static void Respond(Client sender, string text, Color color) {
            DebugConsole.NewMessage(text, color);
#if SERVER
            if (sender != null) { GameMain.Server?.SendConsoleMessage(text, sender, color); }
#endif
        }
    }
}
