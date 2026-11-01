using System;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Spawn {
    public sealed class SpawnClientPlugin : PrimaryPackage.Core.IModule {

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            SpawnConfig.EnsureLoaded();

            PrimaryPackage.Core.ModuleHost.Guard("WayPoint.CreateEditingHUD", () => harmony.Patch(
                AccessTools.Method(typeof(WayPoint), "CreateEditingHUD"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(SleepSpawnEditorPanel), nameof(SleepSpawnEditorPanel.CreateEditingHUDPostfix)))));

            PrimaryPackage.Core.ModuleHost.Guard("WayPoint.Draw", () => harmony.Patch(
                AccessTools.Method(typeof(WayPoint), nameof(WayPoint.Draw),
                    new[] { typeof(SpriteBatch), typeof(bool), typeof(bool) }),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(SleepSpawnEditorOverlay), nameof(SleepSpawnEditorOverlay.WayPointDrawPostfix)))));
        }



        public void Release() {
        }

    }
}
