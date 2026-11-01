#if CLIENT
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.Core;

namespace PrimaryPackage.WallCollision {
    
    
    
    
    public static class WallCollisionPreparation {
        public static void Register() {
            ModCommands.Register(
                "ec_init",
                "ec_init [2|1|off] [left|right|up|down] — prepare selected wall in submarine editor; save the submarine afterwards.",
                Execute,
                localOnClient: true,
                relayToServer: false);
        }

        private static void Execute(string[] args) {
            if (Screen.Selected != GameMain.SubEditorScreen) {
                DebugConsole.NewMessage("ec_init: откройте редактор подлодок.", Color.Yellow);
                return;
            }

            Structure selected = null;
            int count = 0;
            foreach (MapEntity entity in MapEntity.SelectedList) {
                if (entity is not Structure wall) { continue; }
                selected = wall;
                count++;
            }
            if (count != 1) {
                DebugConsole.NewMessage("ec_init: выделите ровно одну стену.", Color.Yellow);
                return;
            }
            if (selected.Removed || !selected.HasBody ||
                !WallGeometry.TryAxis(selected.Bodies, Physics.CollisionWall, out int axis, out Vector2 normal, out _)) {
                DebugConsole.NewMessage("ec_init: выбранная конструкция не имеет подходящей физической стены.", Color.Yellow);
                return;
            }

            string modeArg = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "2";
            if (modeArg == "off") {
                WallConfiguration.Remove(selected);
                DebugConsole.NewMessage("ec_init: настройка стены #" + selected.ID + " удалена. Сохраните подлодку.", Color.LightGreen);
                return;
            }

            WallMode mode;
            if (modeArg == "1" || modeArg == "solid") {
                mode = WallMode.Solid;
            }
            else if (modeArg == "2" || modeArg == "oneway") {
                mode = WallMode.OneWay;
            }
            else {
                DebugConsole.NewMessage("ec_init: режим 1, 2 или off.", Color.Yellow);
                return;
            }

            WallFace face = WallGeometry.FaceOf(axis, true);
            if (args != null && args.Length > 1) {
                Vector2 direction;
                switch (args[1].ToLowerInvariant()) {
                    case "left": case "-x": direction = -Vector2.UnitX; break;
                    case "right": case "+x": direction = Vector2.UnitX; break;
                    case "up": case "+y": direction = Vector2.UnitY; break;
                    case "down": case "-y": direction = -Vector2.UnitY; break;
                    default:
                        DebugConsole.NewMessage("ec_init: направление left/right/up/down.", Color.Yellow);
                        return;
                }
                if (!WallGeometry.TryFaceFromDirection(selected.Bodies, Physics.CollisionWall, direction, out face, out normal)) {
                    DebugConsole.NewMessage("ec_init: направление не перпендикулярно плоскости стены.", Color.Yellow);
                    return;
                }
            }
            WallConfiguration.Prepare(selected, mode, face);
            DebugConsole.NewMessage("ec_init: стена #" + selected.ID + " подготовлена (" +
                (mode == WallMode.Solid ? "двусторонняя" : "односторонняя, " +
                WallGeometry.Describe(normal)) +
                "). До ec_ready действует обычная физика. Сохраните подлодку.", Color.LightGreen);
        }
    }
}
#endif
