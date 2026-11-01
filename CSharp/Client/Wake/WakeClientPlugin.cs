using System;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Wake {
    public sealed class WakeClientPlugin : PrimaryPackage.Core.IModule {
        private const int MaxFailures = 20;

        private Harmony harmony;
        private static int failures;

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            WakeConfig.EnsureLoaded();
            WakePresentation.SetEnabled(PrimaryPackage.Core.ModSettings.WakeIntroEnabled.Value);
            failures = 0;

            PrimaryPackage.Core.HudFrame.Under(PrimaryPackage.Core.HudFrame.Wake, GuiDrawPrefix);

            PrimaryPackage.Core.ModuleHost.Guard("Character.ControlLocalPlayer", () => harmony.Patch(
                AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer)),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WakeClientPlugin), nameof(ControlLocalPlayerPrefix))));

            WakeCommands.Register();
        }



        public void Release() {
            WakeCommands.Unregister();
            WakePresentation.Reset();
            WakeScreen.Give();
            WakeConfig.Reset();
        }

        public static void GuiDrawPrefix(Camera cam, SpriteBatch spriteBatch) {
            if (failures >= MaxFailures) { return; }
            try {
                WakePresentation.Update();
                WakePresentation.Draw(spriteBatch);
                failures = 0;
            } catch (Exception e) {
                failures++;
                WakePresentation.Reset();
                DebugConsole.ThrowError("pp_wake: сбой заставки пробуждения", e);
                if (failures >= MaxFailures) {
                    DebugConsole.ThrowError("pp_wake: заставка остановлена после " + MaxFailures + " сбоев подряд.");
                }
            }
        }

        public static void ControlLocalPlayerPrefix(Character __instance) => WakePresentation.ControlPrefix(__instance);
    }
}
