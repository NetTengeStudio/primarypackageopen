using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Cold {
    public static class ColdCommands {

        public static void Register() {
            Add("pp_cold", "pp_cold: температура отсеков вокруг персонажа.", Report);
            DebugConsole.Command set = Add("pp_cold_set", "pp_cold_set <градусы>: задать температуру текущему отсеку.", Set, relay: true);
#if SERVER
            set.OnClientRequestExecute = SetForClient;
#endif
            Add("coldoff", "coldoff [on|off]: отключить/включить термическую систему.", ColdOff, relay: true);
        }

        public static void Unregister() {
        }

        private static DebugConsole.Command Add(string name, string help, System.Action<string[]> action, bool relay = false) {
            return PrimaryPackage.Core.ModCommands.Register(name, help, action, localOnClient: !relay, relayToServer: relay);
        }

        private static void ColdOff(string[] args) {
            string mode = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : null;
            ColdSubject.Disabled = mode switch {
                "on" => false,
                "off" => true,
                _ => !ColdSubject.Disabled
            };
            DebugConsole.NewMessage(
                "coldoff: термическая система " + (ColdSubject.Disabled ? "отключена." : "включена."),
                ColdSubject.Disabled ? Color.Orange : Color.LightGreen);
        }

        private static Hull Focus() {
#if CLIENT
            Hull hull = Character.Controlled?.CurrentHull;
            if (hull != null) { return hull; }
            Camera cam = Screen.Selected?.Cam;
            return cam == null ? null : Hull.FindHull(cam.WorldViewCenter);
#else
            return null;
#endif
        }

        private static void Report(string[] args) {
            int count = ColdField.Count;
            if (count == 0) {
                DebugConsole.NewMessage("pp_cold: сетка отсеков пуста.", Color.Yellow);
                return;
            }
#if SERVER
            DebugConsole.NewMessage(
                ColdServerNet.LastTickSilent
                    ? $"pp_cold: последний тик без пакета (сообщений всего {ColdServerNet.SentMessages})"
                    : $"pp_cold: последняя рассылка {Timing.TotalTime - ColdServerNet.LastSentAt:0.0} с назад, " +
                      $"{ColdServerNet.LastSentHulls} отс., сообщений всего {ColdServerNet.SentMessages}",
                Color.LightBlue);
#endif
            Hull focus = Focus();
            if (focus == null) {
                float min = float.MaxValue;
                float max = float.MinValue;
                float sum = 0.0f;
                for (int i = 0; i < count; i++) {
                    float t = ColdField.Raw(i);
                    if (t < min) { min = t; }
                    if (t > max) { max = t; }
                    sum += t;
                }
                DebugConsole.NewMessage(
                    $"pp_cold: отсеков {count}, мин {min:0.0}, макс {max:0.0}, средняя {sum / count:0.0}", Color.LightBlue);
                return;
            }
            DebugConsole.NewMessage($"pp_cold: текущий отсек {ColdField.Get(focus):0.0}°", Color.LightBlue);
            foreach (Gap gap in focus.ConnectedGaps) {
                if (gap.linkedTo.Count < 2) { continue; }
                Hull other = gap.linkedTo[0] == focus ? gap.linkedTo[1] as Hull : gap.linkedTo[0] as Hull;
                if (other == null) { continue; }
                DebugConsole.NewMessage(
                    $"  проём {(gap.ConnectedDoor != null ? "дверь" : "проём")} открыт {gap.Open:0.00}, " +
                    $"сечение {gap.Size:0}, соседний отсек {ColdField.Get(other):0.0}°", Color.Gray);
            }
        }

        private static void Set(string[] args) {
            if (args == null || args.Length == 0 ||
                !float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) {
                DebugConsole.NewMessage("pp_cold_set <градусы>", Color.Yellow);
                return;
            }
            Hull focus = Focus();
            if (focus == null) {
                DebugConsole.NewMessage("pp_cold_set: отсек не определён.", Color.Yellow);
                return;
            }
            ColdField.Set(focus, value);
            DebugConsole.NewMessage($"pp_cold_set: {focus.DisplayName} -> {value:0.0}°", Color.LightGreen);
        }

#if SERVER
        private static void SetForClient(Barotrauma.Networking.Client client, Vector2 cursor, string[] args) {
            if (args == null || args.Length == 0 ||
                !float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) {
                GameMain.Server.SendConsoleMessage("pp_cold_set <градусы>", client, Color.Yellow);
                return;
            }
            Hull hull = Hull.FindHull(cursor, client.Character?.CurrentHull) ?? client.Character?.CurrentHull;
            if (hull == null) {
                GameMain.Server.SendConsoleMessage("pp_cold_set: отсек не определён.", client, Color.Yellow);
                return;
            }
            ColdField.Set(hull, value);
            GameMain.Server.SendConsoleMessage($"pp_cold_set: {hull.DisplayName} -> {value:0.0}°", client, Color.LightGreen);
        }
#endif
    }
}
