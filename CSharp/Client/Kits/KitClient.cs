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
    internal static class KitClient {
        public static bool Mp => GameMain.Client != null;

        public static string Text(string key) => TextManager.Get(key).Value;

        public static void RequestList() {
            if (!Mp) { return; }
            PrimaryPackage.Core.ModNet.Send(KitNet.NetId, msg => msg.WriteByte(KitNet.RequestList));
        }

        public static void SendCreate(string id) => SendId(KitNet.Create, id);
        public static void SendDelete(string id) => SendId(KitNet.Delete, id);
        public static void SendExport(string id) => SendId(KitNet.ExportJob, id);

        public static byte Flags(bool control, bool hostile) =>
            (byte)((control ? KitNet.FlagControl : 0) | (hostile ? KitNet.FlagHostile : 0));

        public static void SendSpawn(string id, Vector2 worldPos, bool control, bool hostile) {
            PrimaryPackage.Core.ModNet.Send(KitNet.NetId, msg => {
                msg.WriteByte(KitNet.Spawn);
                msg.WriteString(id);
                msg.WriteSingle(worldPos.X);
                msg.WriteSingle(worldPos.Y);
                msg.WriteByte(Flags(control, hostile));
            });
        }

        private static void SendId(byte header, string id) {
            PrimaryPackage.Core.ModNet.Send(KitNet.NetId, msg => {
                msg.WriteByte(header);
                msg.WriteString(id);
            });
        }

        private static readonly List<(Identifier, XElement)> incoming = new List<(Identifier, XElement)>();

        public static void OnListSync(IReadMessage msg) {
            if (msg.ReadByte() != KitNet.ListSync) { return; }
            byte flags = msg.ReadByte();
            int count = msg.ReadUInt16();
            if (count > KitNet.MaxPerMessage) { return; }
            if ((flags & KitNet.FlagFirst) != 0) { incoming.Clear(); }
            for (int i = 0; i < count; i++) {
                Identifier id = msg.ReadString().ToIdentifier();
                string xml = msg.ReadString();
                XElement el = ParseKit(xml);
                if (!id.IsEmpty && el != null) { incoming.Add((id, el)); }
            }
            if ((flags & KitNet.FlagLast) == 0) { return; }
            KitStore.ReplaceAll(incoming);
            incoming.Clear();
            KitMenu.OnKitsChanged();
        }

        private static XElement ParseKit(string xml) {
            try {
                return XElement.Parse(xml);
            } catch (Exception) {
                return null;
            }
        }

        public static Vector2 CursorWorldPos()
            => Screen.Selected?.Cam?.ScreenToWorld(PlayerInput.MousePosition) ?? Vector2.Zero;

        public static Vector2 MenuSpawnPos()
            => Character.Controlled?.WorldPosition + new Vector2(100, 0)
               ?? GameMain.GameScreen.Cam.WorldViewCenter;

        public static void CreateKit(string id) {
            if (Mp) {
                SendCreate(id);
                return;
            }
            Character character = Character.Controlled;
            if (character?.Info == null) {
                DebugConsole.ThrowError(Text("pp_kits.nocharacter"));
                return;
            }
            Identifier ident = id.ToIdentifier();
            bool existed = KitStore.TryGet(ident, out _);
            if (KitStore.CreateFromCharacter(character, ident)) {
                DebugConsole.NewMessage($"{Text(existed ? "pp_kits.overwritten" : "pp_kits.created")} {ident}", Color.LightGreen);
                KitMenu.OnKitsChanged();
            }
        }

        public static void DeleteKit(string id) {
            if (Mp) {
                SendDelete(id);
                return;
            }
            Identifier ident = id.ToIdentifier();
            DebugConsole.NewMessage(KitStore.Delete(ident)
                ? $"{Text("pp_kits.deleted")} {ident}"
                : $"{Text("pp_kits.notfound")} {ident}", Color.White);
            KitMenu.OnKitsChanged();
        }

        public static void ExportKit(string id) {
            if (Mp) {
                SendExport(id);
                return;
            }
            Identifier ident = id.ToIdentifier();
            if (KitJobExport.Export(ident, out string error)) {
                DebugConsole.NewMessage($"{Text("pp_kits.exported")} {ident}", Color.LightGreen);
            } else {
                DebugConsole.ThrowError($"{Text("pp_kits.exportfailed")} {error}");
            }
        }

        public static void Spawn(string id, Vector2 worldPos, bool control, bool hostile) {
            if (Mp) {
                SendSpawn(id, worldPos, control, hostile);
                return;
            }
            HumanPrefab prefab = KitSpawn.Find(id.ToIdentifier(), out _);
            if (prefab == null) {
                DebugConsole.ThrowError($"{Text("pp_kits.notfound")} {id}");
                return;
            }
            SpawnLocal(prefab, worldPos, control, hostile);
        }

        public static void SpawnLocal(HumanPrefab prefab, Vector2 worldPos, bool control, bool hostile) {
            if (control && hostile) {
                DebugConsole.ThrowError(Text("pp_kits.hostilenopossess"));
                return;
            }
            CharacterTeamType team = Character.Controlled?.TeamID ?? CharacterTeamType.Team1;
            bool queued = KitSpawn.Spawn(prefab, worldPos, team, hostile, c => {
                if (control) { Character.Controlled = c; }
            });
            if (!queued) { DebugConsole.ThrowError(Text("pp_kits.noround")); }
        }
    }
}
