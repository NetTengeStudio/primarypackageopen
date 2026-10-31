#if SERVER
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using PrimaryPackage.Economy;
namespace PrimaryPackage.Commands {
    public sealed class ConsoleCommandInterceptPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            harmony.CreateClassProcessor(typeof(ServerCommandPatch)).Patch();
        }
        public void Release() {
        }
    }
    [HarmonyPatch(typeof(DebugConsole), "ExecuteClientCommand")]
    public static class ServerCommandPatch {
        public static bool Prefix(Client client, Vector2 cursorWorldPos, string command) {
            if (string.IsNullOrWhiteSpace(command)) { return true; }
            string[] split = ToolBox.SplitCommand(command);
            if (split.Length == 0) { return true; }
            string name = split[0].ToLowerInvariant();
            bool isModCommand = name.StartsWith("economy_") || name.StartsWith("ui_") || name == "wiredelete";
            if (isModCommand && client != null && !client.HasPermission(ClientPermissions.ConsoleCommands) && client.Connection != GameMain.Server.OwnerConnection) {
                GameMain.Server?.SendConsoleMessage("You are not permitted to use console commands!", client, Color.Red);
                return false;
            }
            string[] args = split.Skip(1).ToArray();
            switch (name) {
                case "wiredelete": WireDeleteCommand.Execute(args, client); return false;
                case "economy_add": EconomyPlugin.ExecuteAdd(client, args); return false;
                case "economy_set": EconomyPlugin.ExecuteSet(client, args); return false;
                case "economy_history": EconomyPlugin.ExecuteHistory(client, args); return false;
                case "economy_clear_history": EconomyPlugin.ExecuteClearHistory(client); return false;
                case "economy_set_limits": EconomyPlugin.ExecuteSetLimits(client, args); return false;
                case "economy_stats": EconomyPlugin.ExecuteStats(client); return false;
                case "economy_order_manual_approval": OrderBoardPlugin.ExecuteManualApproval(client, args); return false;
                case "economy_order_list": OrderBoardPlugin.ExecuteList(client); return false;
                case "economy_order_approve": OrderBoardPlugin.ExecuteApprove(client, args); return false;
                case "economy_order_reject": OrderBoardPlugin.ExecuteReject(client, args); return false;
                case "economy_dock_lock": OrderBoardPlugin.ExecuteLock(client, args, true); return false;
                case "economy_dock_unlock": OrderBoardPlugin.ExecuteLock(client, args, false); return false;
            }
            return true;
        }
    }
}
#endif
