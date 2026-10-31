using System;
using System.Globalization;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.Bloom {
    internal static class BloomPrefs {
        internal const string ArgsHelp =
            "pp_bloom [on|off|intensity|threshold|knee|multiplier|radius|spread|levels|flash|debris|shock|smoke] [value]";

        internal static readonly string[] ArgNames = {
            "on", "off", "intensity", "threshold", "knee", "multiplier", "radius", "spread", "levels",
            "flash", "debris", "shock", "smoke"
        };

        internal static void ConsoleCommand(string[] args) {
            if (args.Length == 0) {
                Report();
                return;
            }
            string key = args[0].ToLowerInvariant();
            if (key == "on" || key == "off") {
                ModSettings.BloomEnabled.TrySet(key == "on");
                BloomChain.Reconsider();
                DebugConsole.NewMessage($"pp_bloom - свечение {(key == "on" ? "включено" : "выключено")}", Color.Cyan);
                return;
            }
            if (args.Length < 2 || !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) {
                DebugConsole.NewMessage(ArgsHelp, Color.Orange);
                return;
            }
            if (!Assign(key, value)) {
                DebugConsole.NewMessage(ArgsHelp, Color.Orange);
                return;
            }
            BloomChain.Reconsider();
            DebugConsole.NewMessage($"pp_bloom - {key} {value:0.00}", Color.Cyan);
        }

        private static bool Assign(string key, float value) {
            switch (key) {
                case "intensity": return ModSettings.BloomIntensity.TrySet(MathHelper.Clamp(value, 0f, 1f));
                case "threshold": return ModSettings.BloomThreshold.TrySet(MathHelper.Clamp(value, 0f, 1f));
                case "knee": return ModSettings.BloomKnee.TrySet(MathHelper.Clamp(value, 0f, 1f));
                case "multiplier": return ModSettings.BloomMultiplier.TrySet(MathHelper.Clamp(value, 0.1f, 8f));
                case "radius": return ModSettings.BloomRadius.TrySet(MathHelper.Clamp(value, 0.25f, 4f));
                case "spread": return ModSettings.BloomSpread.TrySet(MathHelper.Clamp(value, 0.05f, 0.95f));
                case "levels": return ModSettings.BloomLevels.TrySet(Math.Clamp((int)value, 1, BloomChain.MaxLevels));
                case "flash": return ModSettings.ExplosionFlash.TrySet(MathHelper.Clamp(value, 0f, 4f));
                case "debris": return ModSettings.ExplosionDebris.TrySet(MathHelper.Clamp(value, 0f, 3f));
                case "shock": return ModSettings.ExplosionShock.TrySet(MathHelper.Clamp(value, 0f, 3f));
                case "smoke": return ModSettings.BlastSmoke.TrySet(MathHelper.Clamp(value, 0f, 3f));
                default: return false;
            }
        }

        private static void Report() =>
            DebugConsole.NewMessage(
                $"pp_bloom - свечение {(ModSettings.BloomEnabled.Value ? "включено" : "выключено")}, "
                + $"сила {ModSettings.BloomIntensity.Value:0.00}, порог {ModSettings.BloomThreshold.Value:0.00}, "
                + $"колено {ModSettings.BloomKnee.Value:0.00}, множитель {ModSettings.BloomMultiplier.Value:0.00}, "
                + $"радиус {ModSettings.BloomRadius.Value:0.00}, разброс {ModSettings.BloomSpread.Value:0.00}, "
                + $"уровни {ModSettings.BloomLevels.Value}, вспышка {ModSettings.ExplosionFlash.Value:0.00}, "
                + $"осколки {ModSettings.ExplosionDebris.Value:0.00}, волна {ModSettings.ExplosionShock.Value:0.00}, "
                + $"дым {ModSettings.BlastSmoke.Value:0.00}",
                Color.Cyan);
    }
}
