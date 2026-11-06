using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using PrimaryPackage.Economy;
using HarmonyLib;

namespace PrimaryPackage.Ambient {
    public sealed class AmbientServerPlugin : PrimaryPackage.Core.IModule {
        private const string RelativePath = "Data/pp_ambient_server.xml";
        private static bool enabled = true;
        private static float volume = 1f;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            Load();
            INet.Net.Receive(AmbientNet.RequestId, OnRequest, 2.0);
            Register("pp_ambient_global", "pp_ambient_global [on/off]: включить/выключить эмбиент для всех игроков.", args => {
                enabled = args.Length > 0 ? args[0].Equals("on", StringComparison.OrdinalIgnoreCase) : !enabled;
                Save();
                BroadcastAll();
                DebugConsole.NewMessage($"pp_ambient - глобально {(enabled ? "включён" : "выключен")}", Color.LightGreen);
            });
            Register("pp_ambient_globalvolume", "pp_ambient_globalvolume [0..1]: глобальная громкость эмбиента.", args => {
                if (args.Length < 1 || !float.TryParse(args[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) {
                    DebugConsole.NewMessage($"pp_ambient - текущая глобальная громкость {volume:0.##}", Color.White);
                    return;
                }
                volume = MathHelper.Clamp(v, 0f, 1f);
                Save();
                BroadcastAll();
                DebugConsole.NewMessage($"pp_ambient - глобальная громкость {volume:0.##}", Color.LightGreen);
            });
        }


        public void Release() {
        }

        private void Register(string name, string help, Action<string[]> action) {
            PrimaryPackage.Core.ModCommands.Register(name, help, action);
        }

        private static void OnRequest(IReadMessage msg, Client sender) {
            if (sender?.Connection != null) { SendState(sender.Connection); }
        }

        private static void BroadcastAll() {
            if (GameMain.Server == null) { return; }
            foreach (Client c in GameMain.Server.ConnectedClients) {
                if (c?.Connection != null) { SendState(c.Connection); }
            }
        }

        private static void SendState(NetworkConnection connection) {
            INet.Net.Send(AmbientNet.StateId, connection, msg => {
                msg.WriteBoolean(enabled);
                msg.WriteSingle(volume);
            });
        }

        private static void Load() =>
            PrimaryPackage.Core.ModPrefs.Read(RelativePath, root => {
                enabled = (bool?)root.Attribute("enabled") ?? true;
                volume = MathHelper.Clamp((float?)root.Attribute("volume") ?? 1f, 0f, 1f);
            });

        private static void Save() =>
            PrimaryPackage.Core.ModPrefs.Write(RelativePath, new XElement("AmbientState",
                new XAttribute("enabled", enabled),
                new XAttribute("volume", volume)));
    }
}
