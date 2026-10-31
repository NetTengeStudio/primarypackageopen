using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Wake {
    public static class WakeCommands {
        private static DebugConsole.Command command;

        public static void Register() {
            command = new DebugConsole.Command("pp_wakeup",
                "pp_wakeup [on|off] — заставка пробуждения на станции. Действует только на этом клиенте, off прерывает и текущий показ. По умолчанию включена.",
                Toggle) { RelayToServer = false };
            PrimaryPackage.Core.ModCommands.Adopt(command);
        }

        public static void Unregister() {
            if (command == null) { return; }
            PrimaryPackage.Core.ModCommands.Drop(command);
            command = null;
        }

        private static void Toggle(string[] args) {
            string mode = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : null;
            WakePresentation.SetEnabled(mode switch {
                "on" or "1" or "true" => true,
                "off" or "0" or "false" => false,
                _ => !WakePresentation.Enabled
            });
            DebugConsole.NewMessage(
                "pp_wakeup: " + (WakePresentation.Enabled ? "включена" : "выключена")
                + (WakeGate.Privileged ? ", подавлена правами" : string.Empty)
                + ", " + WakePresentation.Diag(),
                WakePresentation.Enabled ? Color.LightGreen : Color.Orange);
        }
    }
}
