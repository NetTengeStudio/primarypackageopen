using System;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Thoughts {
    public sealed class ThoughtsPlugin : PrimaryPackage.Core.IModule {
        private const string CommandName = "pp_thoughts";

        private static readonly string[] Arguments = { "status", "test" };

        private DebugConsole.Command? command;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            PrimaryPackage.Core.HudFrame.Over(PrimaryPackage.Core.HudFrame.Thought, GuiDrawPostfix);
            command = new DebugConsole.Command(CommandName,
                CommandName + " [status|test <категория>] — диагностика внутренних реплик",
                Execute, () => new[] { Arguments }) {
                OnClientExecute = Execute,
                RelayToServer = false
            };
            PrimaryPackage.Core.ModCommands.Adopt(command);
        }



        public void Release() {
            if (command != null) {
                PrimaryPackage.Core.ModCommands.Drop(command);
                command = null;
            }
            ThoughtHud.Reset();
        }

        public static void GuiDrawPostfix(Camera cam, SpriteBatch spriteBatch) => ThoughtHud.Draw(spriteBatch);

        private static void Execute(string[] args) {
            string? mode = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : null;
            switch (mode) {
                case null:
                case "":
                case "status":
                    PrintStatus();
                    return;
                case "test":
                    Test(args != null && args.Length > 1 ? args[1] : "");
                    return;
                default:
                    DebugConsole.NewMessage("Неизвестный аргумент: " + mode, Color.Yellow);
                    return;
            }
        }

        private static void PrintStatus() {
            ThoughtSignals s = ThoughtHud.Signals;
            DebugConsole.NewMessage(
                $"боль {s.Pain:F1} | кровопотеря {s.Bloodloss:F1} | голод {s.Hunger:F1} | мозг {s.Brain:F2} | " +
                $"страх {s.Fear:F2} | живучесть {s.Vitality:P0} | холод {s.Cold:P0}", Color.LightBlue);
            DebugConsole.NewMessage(
                $"флаги [{s.Flags}] | тяжесть {s.Severity:F2} | вес {ThoughtSelector.Weight(s):F2} | " +
                $"накопитель {ThoughtHud.Gate:F2}", Color.LightBlue);
        }

        private static void Test(string name) {
            if (string.IsNullOrWhiteSpace(name)) {
                DebugConsole.NewMessage("Категории: " + string.Join(", ", Enum.GetNames(typeof(ThoughtCategory))),
                    Color.Yellow);
                return;
            }
            if (!Enum.TryParse(name, true, out ThoughtCategory category) || category == ThoughtCategory.None) {
                DebugConsole.NewMessage("Нет такой категории: " + name, Color.Yellow);
                return;
            }
            if (!ThoughtHud.Force(category)) {
                DebugConsole.NewMessage("Для категории " + category + " не загружено ни одной фразы.", Color.Yellow);
            }
        }
    }
}
