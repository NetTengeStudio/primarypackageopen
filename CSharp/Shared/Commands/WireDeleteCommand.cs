using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using HarmonyLib;

namespace PrimaryPackage.Commands {
    public sealed class WireDeletePlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            WireDeleteCommand.Register();
        }
        public void Release() {
            WireDeleteCommand.Unregister();
        }
    }

    public static class WireDeleteCommand {
        private static DebugConsole.Command command;

        public static void Register() {
            if (command != null) return;
            
            command = new DebugConsole.Command(
                "wiredelete",
                "wiredelete [loose|all]: Удаляет только мусорные обрывки проводов со стен. Не трогает провода в виде предметов (в инвентаре/руках/шкафах/катушках).",
                (string[] args) => Execute(args)
            );
#if CLIENT
            command.RelayToServer = true;
#endif
#if SERVER
            command.OnClientRequestExecute = (Client client, Vector2 cursor, string[] args) => {
                if (client != null && !client.HasPermission(ClientPermissions.ConsoleCommands) && client.Connection != GameMain.Server.OwnerConnection) {
                    GameMain.Server?.SendConsoleMessage("У вас нет прав для выполнения команды wiredelete!", client, Color.Red);
                    return;
                }
                Execute(args, client);
            };
#endif
            PrimaryPackage.Core.ModCommands.Adopt(command);
        }

        public static void Unregister() {
            if (command != null) {
                PrimaryPackage.Core.ModCommands.Drop(command);
                command = null;
            }
        }

        public static void Execute(string[] args, Client senderClient = null) {
            string mode = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "loose";
            bool includeItemsAndInventory = mode == "all" || mode == "full" || mode == "items";

            List<Item> wiresToRemove = new List<Item>();
            int totalWiresFound = 0;
            int skippedItemFormCount = 0;

            var items = Item.ItemList.ToList();

            foreach (var item in items) {
                if (item == null || item.Removed) continue;

                var wire = item.GetComponent<Wire>();
                if (wire == null) continue;

                totalWiresFound++;

                bool isConnected = IsWireConnected(wire);
                if (isConnected) continue;

                bool inInventory = item.ParentInventory != null || item.Container != null;
                var wireNodes = wire.GetNodes();
                bool isItemForm = inInventory || (wireNodes == null || wireNodes.Count <= 1);

                if (isItemForm && !includeItemsAndInventory) {
                    skippedItemFormCount++;
                    continue;
                }

                wiresToRemove.Add(item);
            }

            int count = wiresToRemove.Count;
            for (int i = 0; i < wiresToRemove.Count; i++) {
                var item = wiresToRemove[i];
                if (item == null || item.Removed) continue;

#if SERVER
                if (Entity.Spawner != null) {
                    Entity.Spawner.AddEntityToRemoveQueue(item);
                } else {
                    item.Remove();
                }
#else
                item.Remove();
#endif
            }

            string msg = $"удалено {count} проводов";
            
            SendMessage(msg, Color.LightGreen, senderClient);
        }

        private static bool IsWireConnected(Wire wire) {
            if (wire == null) return false;

            if (wire.Connections != null) {
                for (int i = 0; i < wire.Connections.Length; i++) {
                    var conn = wire.Connections[i];
                    if (conn != null) {
                        return true;
                    }
                }
            }

            if (wire.Item != null && wire.Item.Connections != null) {
                for (int i = 0; i < wire.Item.Connections.Count; i++) {
                    var conn = wire.Item.Connections[i];
                    if (conn != null && conn.Wires != null && conn.Wires.Contains(wire)) {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void SendMessage(string text, Color color, Client client = null) {
            DebugConsole.NewMessage(text, color);
#if SERVER
            if (client != null && GameMain.Server != null) {
                GameMain.Server.SendConsoleMessage(text, client, color);
            }
#endif
        }
    }
}
