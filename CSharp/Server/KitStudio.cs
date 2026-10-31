using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using PrimaryPackage.Economy;
using HarmonyLib;

namespace PrimaryPackage.Kits {
    public sealed class KitServerPlugin : PrimaryPackage.Core.IModule, PrimaryPackage.Core.ILoadCompleted {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            PrimaryPackage.Core.ModNet.Receive(KitNet.NetId, OnMessage, 0.5);
        }

        public void LoadCompleted() => KitStore.Load();
        public void Release() {}

        private static bool Allowed(Client c)
            => c != null && (c.HasPermission(ClientPermissions.ConsoleCommands) || c.Connection == GameMain.Server?.OwnerConnection);

        private static string Text(string key) => TextManager.Get(key).Value;

        private static void OnMessage(IReadMessage msg, Client sender) {
            if (sender == null) { return; }
            byte header = msg.ReadByte();
            if (header == KitNet.RequestList) {
                SendList(sender);
                return;
            }
            if (!Allowed(sender)) {
                HintSender.Error(sender, Text("pp_kits.nopermission"));
                return;
            }
            switch (header) {
                case KitNet.Create: HandleCreate(msg, sender); break;
                case KitNet.Delete: HandleDelete(msg, sender); break;
                case KitNet.Spawn: HandleSpawn(msg, sender); break;
                case KitNet.ExportJob: HandleExport(msg, sender); break;
            }
        }

        private static void HandleCreate(IReadMessage msg, Client sender) {
            Identifier id = msg.ReadString().ToIdentifier();
            Character character = sender.Character;
            if (character?.Info == null) {
                HintSender.Error(sender, Text("pp_kits.nocharacter"));
                return;
            }
            bool existed = KitStore.TryGet(id, out _);
            if (!KitStore.CreateFromCharacter(character, id)) {
                HintSender.Error(sender, Text("pp_kits.createfailed"));
                return;
            }
            BroadcastList();
            HintSender.Success(sender, Text(existed ? "pp_kits.overwritten" : "pp_kits.created") + " " + id.Value);
        }

        private static void HandleDelete(IReadMessage msg, Client sender) {
            Identifier id = msg.ReadString().ToIdentifier();
            if (!KitStore.Delete(id)) {
                HintSender.Error(sender, Text("pp_kits.notfound") + " " + id.Value);
                return;
            }
            BroadcastList();
            HintSender.Success(sender, Text("pp_kits.deleted") + " " + id.Value);
        }

        private static void HandleSpawn(IReadMessage msg, Client sender) {
            Identifier id = msg.ReadString().ToIdentifier();
            float x = msg.ReadSingle();
            float y = msg.ReadSingle();
            byte flags = msg.ReadByte();
            HumanPrefab prefab = KitSpawn.Find(id, out _);
            if (prefab == null) {
                HintSender.Error(sender, Text("pp_kits.notfound") + " " + id.Value);
                return;
            }
            bool control = (flags & KitNet.FlagControl) != 0;
            bool hostile = (flags & KitNet.FlagHostile) != 0;
            if (control && hostile) {
                HintSender.Error(sender, Text("pp_kits.hostilenopossess"));
                return;
            }
            CharacterTeamType team = sender.Character?.TeamID ?? CharacterTeamType.Team1;
            bool queued = KitSpawn.Spawn(prefab, new Vector2(x, y), team, hostile, c => {
                c.LoadTalents();
                GameMain.NetworkMember.CreateEntityEvent(c, new Character.UpdateTalentsEventData());
                if (control && GameMain.Server != null && GameMain.Server.ConnectedClients.Contains(sender)) {
                    GameMain.Server.SetClientCharacter(sender, c);
                }
            });
            if (!queued) {
                HintSender.Error(sender, Text("pp_kits.noround"));
                return;
            }
            HintSender.Success(sender, Text("pp_kits.spawned") + " " + id.Value);
        }

        private static void HandleExport(IReadMessage msg, Client sender) {
            Identifier id = msg.ReadString().ToIdentifier();
            if (KitJobExport.Export(id, out string error)) {
                HintSender.Success(sender, Text("pp_kits.exported") + " " + id.Value);
            } else {
                HintSender.Error(sender, Text("pp_kits.exportfailed") + " " + error);
            }
        }

        private static void SendList(Client c) {
            var entries = new List<(string Id, string Xml)>(KitStore.Count);
            foreach (KeyValuePair<Identifier, KitStore.Kit> pair in KitStore.All) {
                entries.Add((pair.Key.Value, pair.Value.Element.ToString(SaveOptions.DisableFormatting)));
            }
            int offset = 0;
            bool first = true;
            do {
                int start = offset;
                int size = 0;
                int bytes = 0;
                while (start + size < entries.Count && size < KitNet.MaxPerMessage
                       && (size == 0 || bytes < KitNet.MaxMessageBytes)) {
                    bytes += entries[start + size].Id.Length + entries[start + size].Xml.Length;
                    size++;
                }
                bool last = start + size >= entries.Count;
                byte flags = (byte)((first ? KitNet.FlagFirst : 0) | (last ? KitNet.FlagLast : 0));
                PrimaryPackage.Core.ModNet.Send(KitNet.NetId, c.Connection, msg => {
                    msg.WriteByte(KitNet.ListSync);
                    msg.WriteByte(flags);
                    msg.WriteUInt16((ushort)size);
                    for (int i = 0; i < size; i++) {
                        msg.WriteString(entries[start + i].Id);
                        msg.WriteString(entries[start + i].Xml);
                    }
                });
                first = false;
                offset = start + size;
            } while (offset < entries.Count);
        }

        private static void BroadcastList() {
            if (GameMain.Server == null) { return; }
            foreach (Client c in GameMain.Server.ConnectedClients) { SendList(c); }
        }
    }
}
