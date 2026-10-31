using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision
{
    // A prepared wall is editor/map metadata, not a live physics override.
    // Live rules are activated in one server-authoritative operation by ec_ready.
    internal static class WallPreparations
    {
        private const string FaceAttribute = "pp_ecface";
        private sealed class Entry
        {
            internal Structure Wall;
            internal WallFace Face;
        }

        private static readonly Dictionary<Structure, Entry> entries =
            new Dictionary<Structure, Entry>(ReferenceEqualityComparer.Instance);

        internal static bool Ready { get; private set; }
        internal static int Count => entries.Count;

        internal static void Install(Harmony harmony)
        {
            PrimaryPackage.Core.ModuleHost.Guard("ec structure serialization", () =>
            {
                var load = AccessTools.Method(typeof(Structure), nameof(Structure.Load));
                var save = AccessTools.Method(typeof(Structure), nameof(Structure.Save));
                if (load == null || save == null)
                {
                    throw new MissingMethodException("Structure.Load or Structure.Save");
                }
                harmony.Patch(load,
                    postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallPreparations), nameof(Loaded)));
                harmony.Patch(save,
                    postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallPreparations), nameof(Saved)));
            });

            PrimaryPackage.Core.ModCommands.Register("ec_init",
                "ec_init [left|right|up|down|off]: prepare the selected editor wall for ec_ready.",
                InitCommand, localOnClient: true, relayToServer: false);

            DebugConsole.Command ready = PrimaryPackage.Core.ModCommands.Register("ec_ready",
                "ec_ready: activate the prepared collision walls for the current round.",
                ReadyCommand, localOnClient: false, relayToServer: true);
#if SERVER
            if (ready != null) { ready.OnClientRequestExecute = ReadyForClient; }
#endif
        }

        public static void Loaded(ContentXElement element, Structure __result)
        {
            if (__result == null || !ValidWall(__result)) { return; }
            string raw = element?.GetAttributeString(FaceAttribute, null);
            if (raw == null || !byte.TryParse(raw, out byte face) || face > (byte)WallFace.MinusY) { return; }
            Stage(__result, (WallFace)face);
        }

        public static void Saved(Structure __instance, XElement __result)
        {
            if (__result == null || __instance == null) { return; }
            if (entries.TryGetValue(__instance, out Entry entry))
            {
                __result.SetAttributeValue(FaceAttribute, (byte)entry.Face);
            }
        }

        internal static bool Stage(Structure wall, WallFace face)
        {
            if (!ValidWall(wall) || face > WallFace.MinusY) { return false; }
            entries[wall] = new Entry { Wall = wall, Face = face };
            return true;
        }

        internal static bool Forget(Structure wall) => wall != null && entries.Remove(wall);

        internal static void Disarm() => Ready = false;

        internal static int Activate()
        {
            if (Ready) { return -1; }
            Ready = true;
            int applied = 0;
            foreach (Entry entry in entries.Values)
            {
                Structure wall = entry.Wall;
                if (!ValidWall(wall) || !ReferenceEquals(Entity.FindEntityByID(wall.ID), wall)) { continue; }
                WallRules.Bind(wall, WallMode.OneWay, entry.Face);
                applied++;
            }
            return applied;
        }

        internal static void Removed(Structure wall) => Forget(wall);

        internal static void Reset()
        {
            Ready = false;
            entries.Clear();
        }

        private static bool ValidWall(Structure wall)
            => wall != null && !wall.Removed && wall.HasBody && !wall.IsPlatform &&
               wall.Bodies != null && wall.Bodies.Count > 0;

        private static void InitCommand(string[] args)
        {
#if CLIENT
            if (GameMain.Client != null || Screen.Selected is not { IsEditor: true })
            {
                Reply("ec_init: команда работает только в локальном редакторе подлодок.", Color.Yellow);
                return;
            }
            // MapEntity.SelectedList is a HashSet<MapEntity> in the client,
            // not an indexable list; require exactly one selection.
            Structure wall = null;
            if (MapEntity.SelectedList.Count == 1)
            {
                foreach (MapEntity selected in MapEntity.SelectedList)
                {
                    wall = selected as Structure;
                    break;
                }
            }
            if (!ValidWall(wall))
            {
                Reply("ec_init: выделите одну физическую стену (не декорацию и не платформу).", Color.Yellow);
                return;
            }
            string arg = args != null && args.Length > 0 ? args[0]?.Trim().ToLowerInvariant() : null;
            if (arg is "off" or "0")
            {
                Reply(Forget(wall) ? "ec_init: подготовка стены снята." : "ec_init: стена не была подготовлена.", Color.LightGreen);
                return;
            }
            WallFace face;
            Vector2 normal;
            if (string.IsNullOrEmpty(arg))
            {
                Camera camera = Screen.Selected.Cam;
                Vector2 point = camera.ScreenToWorld(PlayerInput.MousePosition);
                if (!WallGeometry.TryFaceFromWorldPoint(wall.Bodies, Physics.CollisionWall, point, true, out face, out normal))
                {
                    Reply("ec_init: невозможно определить сторону физической стены.", Color.Yellow);
                    return;
                }
            }
            else if (WallCollisionCommand.TryDirection(arg, out Vector2 direction) &&
                     WallGeometry.TryFaceFromDirection(wall.Bodies, Physics.CollisionWall, direction, out face, out normal))
            {
                // The explicit direction is relative to the wall's actual axis.
            }
            else
            {
                Reply("ec_init: используйте left, right, up, down либо off.", Color.Yellow);
                return;
            }
            if (!Stage(wall, face)) { Reply("ec_init: настройка стены отклонена.", Color.Red); return; }
            Reply($"ec_init: стена #{wall.ID} подготовлена, проход {WallGeometry.Describe(normal)}. Сохраните .sub; включение — ec_ready в раунде.", Color.LightGreen);
#else
            Reply("ec_init: нужна выбранная стена в редакторе клиента.", Color.Yellow);
#endif
        }

#if SERVER
        private static void ReadyForClient(Barotrauma.Networking.Client client, Vector2 cursor, string[] args)
        {
            if (client == null || !PrimaryPackage.MpPermissionGuard.CheckOrWarn(
                    client, Barotrauma.Networking.ClientPermissions.ConsoleCommands, "ec_ready"))
            {
                return;
            }
            string result = ActivateInRound();
            GameMain.Server?.SendConsoleMessage(result, client, Color.LightGreen);
        }
#endif

        private static void ReadyCommand(string[] args)
        {
#if CLIENT
            if (GameMain.Client != null)
            {
                Reply("ec_ready: команду выполняет сервер.", Color.Yellow);
                return;
            }
#endif
            Reply(ActivateInRound(), Color.LightGreen);
        }

        private static string ActivateInRound()
        {
            if (GameMain.GameSession is not { IsRunning: true }) { return "ec_ready: раунд не запущен."; }
            int applied = Activate();
            if (applied < 0) { return "ec_ready: правила уже активированы."; }
#if SERVER
            WallCollisionServer.BroadcastSnapshot();
#endif
            return $"ec_ready: активировано подготовленных стен {applied}.";
        }

        private static void Reply(string value, Color color) => DebugConsole.NewMessage(value, color);
    }
}
