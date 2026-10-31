using System;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.WallCollision {
    public static class WallCollisionCommand {
        private const string Usage =
            "ec 1|2|off — коллизия стены под курсором: 1 блокирует с обеих сторон, 2 пропускает с выбранной стороны, off снимает переопределение.";
        private const string UsageSide =
            "ec 2 [left|right|up|down|+x|-x|+y|-y|flip] — с какой стороны разрешён проход; ec list — активные переопределения.";

        public static void Register() {
            DebugConsole.Command command = PrimaryPackage.Core.ModCommands.Register(
                "ec", Usage, OnLocalExecute, localOnClient: false, relayToServer: true);
#if SERVER
            if (command != null) { command.OnClientRequestExecute = OnClientRequest; }
#endif
        }

#if SERVER
        private static void OnClientRequest(Client client, Vector2 cursorWorld, string[] args) {
            if (client == null) { return; }
            if (!PrimaryPackage.MpPermissionGuard.CheckOrWarn(client, ClientPermissions.ConsoleCommands, "ec")) {
                SendTo(client, "ec: нужны права на консольные команды.", Color.Red);
                return;
            }
            if (GameMain.GameSession == null) {
                SendTo(client, "ec: раунд не запущен.", Color.Yellow);
                return;
            }
            if (!MathUtils.IsValid(cursorWorld)) {
                SendTo(client, "ec: клиент прислал некорректную позицию курсора.", Color.Red);
                return;
            }
            Character character = client.Character;
            bool hasSide = character != null;
            Vector2 side = hasSide ? character.WorldPosition : cursorWorld;
            RuleChange change = Apply(args, cursorWorld, side, hasSide, out string text);
            SendTo(client, text, change.Changed ? Color.LightGreen : Color.White);
            if (!change.Changed) { return; }
            WallCollisionServer.Broadcast(change);
        }

        private static void SendTo(Client client, string text, Color color) {
            if (string.IsNullOrEmpty(text) || GameMain.Server == null) { return; }
            int start = 0;
            for (int i = 0; i <= text.Length; i++) {
                if (i < text.Length && text[i] != '\n') { continue; }
                if (i > start) { GameMain.Server.SendConsoleMessage(text.Substring(start, i - start), client, color); }
                start = i + 1;
            }
        }
#endif

        private static void OnLocalExecute(string[] args) {
#if CLIENT
            if (GameMain.GameSession == null) {
                Reply("ec: раунд не запущен.", Color.Yellow);
                return;
            }
            if (GameMain.Client != null) {
                Reply("ec: в мультиплеере команду применяет сервер, локально она не выполняется.", Color.Yellow);
                return;
            }
            Camera camera = GameMain.GameScreen?.Cam;
            if (camera == null || Screen.Selected != GameMain.GameScreen) {
                Reply("ec: команда работает в игре, а не в меню.", Color.Yellow);
                return;
            }
            Character controlled = Character.Controlled;
            bool hasSide = controlled != null;
            Vector2 side = hasSide ? controlled.WorldPosition : camera.WorldViewCenter;
            RuleChange change = Apply(args, camera.ScreenToWorld(PlayerInput.MousePosition), side, hasSide, out string text);
            Reply(text, change.Changed ? Color.LightGreen : Color.White);
#else
            Reply("ec: у серверной консоли нет курсора — введите команду в игре, администратором с открытым интерфейсом.", Color.Yellow);
#endif
        }

        internal static RuleChange Apply(string[] args, Vector2 cursorWorld, Vector2 sideWorld, bool hasSide, out string text) {
            text = null;
            if (GameMain.GameSession == null) {
                text = "ec: раунд не запущен.";
                return RuleChange.None;
            }
            if (args == null || args.Length == 0) {
                text = Describe(cursorWorld);
                return RuleChange.None;
            }
            string verb = args[0] == null ? string.Empty : args[0].Trim().ToLowerInvariant();
            if (verb == "list" || verb == "ls") {
                text = List();
                return RuleChange.None;
            }
            Structure wall = WallPicker.Pick(cursorWorld);
            if (wall == null) {
                text = "ec: под курсором нет стены со сплошной коллизией; декор, платформы и полностью разрушенные стены не подходят.";
                return RuleChange.None;
            }
            if (verb == "1" || verb == "solid" || verb == "both") {
                if (Same(wall, WallMode.Solid, WallFace.PlusX)) {
                    text = "ec 1: стена «" + wall.Name + "» #" + wall.ID + " уже блокирует с обеих сторон.";
                    return RuleChange.None;
                }
                WallRules.Bind(wall, WallMode.Solid, WallFace.PlusX);
                text = "ec 1: стена «" + wall.Name + "» #" + wall.ID +
                       " — блокирует с обеих сторон (родная физика, направленный фильтр снят).";
                return new RuleChange(wall.ID, WallMode.Solid, WallFace.PlusX);
            }
            if (verb == "0" || verb == "off" || verb == "none") {
                bool had = WallRules.Unbind(wall.ID);
                text = had
                    ? "ec off: стена «" + wall.Name + "» #" + wall.ID + " — переопределение снято, физика стены родная."
                    : "ec off: у стены «" + wall.Name + "» #" + wall.ID + " переопределения не было.";
                return had ? new RuleChange(wall.ID, WallMode.None, WallFace.PlusX) : RuleChange.None;
            }
            if (verb == "2" || verb == "oneway" || verb == "one-way") {
                return ApplyOneWay(wall, args, sideWorld, hasSide, out text);
            }
            text = "ec: неизвестный режим «" + args[0] + "». " + Usage;
            return RuleChange.None;
        }

        private static RuleChange ApplyOneWay(Structure wall, string[] args, Vector2 sideWorld, bool hasSide, out string text) {
            text = null;
            WallFace face;
            Vector2 normal;
            string sideArg = args.Length > 1 && args[1] != null ? args[1].Trim().ToLowerInvariant() : string.Empty;
            if (sideArg.Length == 0) {
                if (!WallGeometry.TryFaceFromPoint(wall.Bodies, Physics.CollisionWall, sideWorld, hasSide, out face, out normal)) {
                    text = "ec 2: у стены нет сплошных участков.";
                    return RuleChange.None;
                }
            }
            else if (sideArg == "flip" || sideArg == "invert") {
                if (!WallGeometry.TryFaceFromPoint(wall.Bodies, Physics.CollisionWall, sideWorld, hasSide, out face, out normal)) {
                    text = "ec 2: у стены нет сплошных участков.";
                    return RuleChange.None;
                }
                face = WallGeometry.Opposite(face);
                normal = -normal;
            }
            else {
                if (!TryDirection(sideArg, out Vector2 direction)) {
                    text = "ec 2: неизвестная сторона «" + args[1] + "». " + UsageSide;
                    return RuleChange.None;
                }
                if (!WallGeometry.TryFaceFromDirection(wall.Bodies, Physics.CollisionWall, direction, out face, out normal)) {
                    string faces = "?";
                    if (WallGeometry.TryAxis(wall.Bodies, Physics.CollisionWall, out _, out Vector2 axisWorld, out _)) {
                        faces = WallGeometry.Describe(axisWorld) + " или " + WallGeometry.Describe(-axisWorld);
                    }
                    text = "ec 2: сторона «" + args[1] + "» не проходит сквозь стену. Доступны: " + faces + ".";
                    return RuleChange.None;
                }
            }
            if (Same(wall, WallMode.OneWay, face)) {
                text = "ec 2: стена «" + wall.Name + "» #" + wall.ID + " уже пропускает со стороны " +
                       WallGeometry.Describe(normal) + ".";
                return RuleChange.None;
            }
            WallRules.Bind(wall, WallMode.OneWay, face);
            text = "ec 2: стена «" + wall.Name + "» #" + wall.ID + " — проход со стороны " +
                   WallGeometry.DescribeExact(normal) + ", обратное пересечение запрещено.";
            return new RuleChange(wall.ID, WallMode.OneWay, face);
        }

        private static bool Same(Structure wall, WallMode mode, WallFace face) {
            WallRule rule = WallRules.Find(wall);
            return rule != null && rule.Mode == mode && rule.Face == face;
        }

        private static bool TryDirection(string text, out Vector2 direction) {
            switch (text) {
                case "left":
                case "-x":
                    direction = new Vector2(-1f, 0f);
                    return true;
                case "right":
                case "+x":
                    direction = new Vector2(1f, 0f);
                    return true;
                case "up":
                case "+y":
                    direction = new Vector2(0f, 1f);
                    return true;
                case "down":
                case "-y":
                    direction = new Vector2(0f, -1f);
                    return true;
                default:
                    direction = Vector2.Zero;
                    return false;
            }
        }

        private static string Describe(Vector2 cursorWorld) {
            Structure wall = WallPicker.Pick(cursorWorld);
            if (wall == null) {
                return "ec: под курсором нет стены со сплошной коллизией. " + Usage;
            }
            string head = "ec: стена «" + wall.Name + "» #" + wall.ID + " — ";
            WallRule rule = WallRules.Find(wall);
            if (rule == null) { return head + "переопределения нет (родная двусторонняя коллизия)."; }
            if (rule.Mode == WallMode.Solid) { return head + "принудительно двусторонняя (родная физика)."; }
            return head + "проход со стороны " + WallGeometry.Describe(rule.Normal) +
                   ", участков под фильтром: " + rule.GatedBodies + ".";
        }

        private static string List() {
            int count = WallRules.Count;
            string text = "ec: переопределений " + count + ".";
#if CLIENT
            if (GameMain.Client != null) { text += "\n  синхронизация: " + WallCollisionClient.Status() + "."; }
#endif
            for (int i = 0; i < count; i++) {
                WallRule rule = WallRules.At(i);
                Structure wall = rule.Wall;
                string name = wall == null ? "?" : wall.Name;
                text += "\n  #" + rule.Id + " «" + name + "» — ";
                if (rule.Mode == WallMode.Solid) {
                    text += "двусторонняя";
                }
                else {
                    text += "проход " + WallGeometry.Describe(rule.Normal) + " (участков " + rule.GatedBodies + ")";
                }
            }
            return text;
        }

        private static void Reply(string text, Color color) {
            if (string.IsNullOrEmpty(text)) { return; }
            int start = 0;
            for (int i = 0; i <= text.Length; i++) {
                if (i < text.Length && text[i] != '\n') { continue; }
                if (i > start) { DebugConsole.NewMessage(text.Substring(start, i - start), color); }
                start = i + 1;
            }
        }
    }
}
