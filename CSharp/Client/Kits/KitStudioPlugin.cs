using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Kits {
    public sealed class KitStudioPlugin : PrimaryPackage.Core.IModule, PrimaryPackage.Core.ILoadCompleted {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            PrimaryPackage.Core.ModNet.Receive(KitNet.NetId, KitClient.OnListSync);
            Register("createkit", "createkit [имя]: сохранить текущего персонажа (профессия, внешность, инвентарь) как кит.", args => {
                if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0])) {
                    DebugConsole.ThrowError(KitClient.Text("pp_kits.needname"));
                    return;
                }
                KitClient.CreateKit(args[0]);
            });
            Register("spawnkit", "spawnkit [имя] [npc|hostile]: заспавнить кит или НПС из любого npcset под курсором; 'npc', оставить под ИИ; 'hostile', враждебный.", args => {
                if (args.Length < 1) {
                    DebugConsole.ThrowError(KitClient.Text("pp_kits.needname"));
                    return;
                }
                string mode = args.Length > 1 ? args[1] : string.Empty;
                bool hostile = mode.Equals("hostile", StringComparison.OrdinalIgnoreCase);
                bool control = !hostile && !mode.Equals("npc", StringComparison.OrdinalIgnoreCase);
                KitClient.Spawn(args[0], KitClient.CursorWorldPos(), control, hostile);
            });
            Register("deletekit", "deletekit [имя]: удалить кит.", args => {
                if (args.Length < 1) { return; }
                KitClient.DeleteKit(args[0]);
            });
            Register("kitjob", "kitjob [имя]: экспортировать кит как профессию в Data/pp_jobs.xml.", args => {
                if (args.Length < 1) { return; }
                KitClient.ExportKit(args[0]);
            });
            Register("kits", "kits: открыть меню китов и ролей НПС.", _ => KitMenu.Toggle());
        }

        public void LoadCompleted() {
            if (!KitClient.Mp) { KitStore.Load(); }
        }


        public void Release() {

        }

        private void Register(string name, string help, Action<string[]> action) {
            PrimaryPackage.Core.ModCommands.Register(name, help, action);
        }

    }
}
