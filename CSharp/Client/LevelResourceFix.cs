using System;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;

namespace PrimaryPackage.Fixes {
    public sealed class LevelResourceFixPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            var target = AccessTools.Method(typeof(LevelResource), nameof(LevelResource.ClientEventRead), new[] { typeof(IReadMessage), typeof(float) });
            if (target != null) {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(LevelResourceFixPlugin), nameof(ClientEventReadPrefix)));
            }
        }


        public void Release() {
        }

        private static readonly FieldInfo deattachTimerField = AccessTools.Field(typeof(LevelResource), "deattachTimer");
        private static readonly FieldInfo holdableField = AccessTools.Field(typeof(LevelResource), "holdable");
        private static readonly FieldInfo triggerField = AccessTools.Field(typeof(LevelResource), "trigger");

        public static bool ClientEventReadPrefix(LevelResource __instance, IReadMessage msg, float sendingTime) {
            float timer = msg.ReadSingle();
            deattachTimerField?.SetValue(__instance, timer);
            if (timer >= __instance.DeattachDuration) {
                Holdable holdable = holdableField?.GetValue(__instance) as Holdable;
                holdable?.DeattachFromWall();
                PhysicsBody trigger = triggerField?.GetValue(__instance) as PhysicsBody;
                if (trigger != null) {
                    trigger.Enabled = false;
                }
            }
            return false;
        }
    }
}
