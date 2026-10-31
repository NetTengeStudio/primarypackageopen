using System;
using System.Reflection;
using Barotrauma;
using HarmonyLib;

namespace PrimaryPackage.WallCollision {
    public sealed class WallCollisionPlugin : PrimaryPackage.Core.IModule {
        public void Attach(Harmony harmony) {
            WallCollisionCommand.Register();
#if CLIENT
            // Snapshot retries need an update tick only on the client. No wall-rule polling.
            PrimaryPackage.Core.ModuleHost.Guard("ec client sync", () => harmony.Patch(
                AccessTools.Method(typeof(GameSession), nameof(GameSession.Update)),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallCollisionPlugin), nameof(SessionUpdatePostfix))));
#endif
            PrimaryPackage.Core.ModuleHost.Guard("ec lifecycle", () => {
                HarmonyMethod removing = PrimaryPackage.Core.ModuleHost.Bind(
                    typeof(WallCollisionPlugin), nameof(StructureRemovingPrefix));
                harmony.Patch(AccessTools.Method(typeof(Structure), nameof(Structure.Remove)), prefix: removing);
                harmony.Patch(AccessTools.Method(typeof(Structure), nameof(Structure.ShallowRemove)), prefix: removing);
                harmony.Patch(AccessTools.Method(typeof(GameSession), nameof(GameSession.EndRound)),
                    postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallCollisionPlugin), nameof(EndRoundPostfix)));
            });
            PrimaryPackage.Core.ModuleHost.Guard("ec sections", () => {
                MethodInfo target = AccessTools.Method(typeof(Structure), "UpdateSections");
                if (target == null) { throw new MissingMethodException("Structure.UpdateSections"); }
                harmony.Patch(target,
                    postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallCollisionPlugin), nameof(SectionsPostfix)));
            });
#if SERVER
            WallCollisionServer.Hook();
#else
            WallCollisionClient.Hook();
#endif
        }

        public void RoundStart() {
            WallRules.Reset();
#if SERVER
            WallCollisionServer.OnRoundStart();
#else
            WallCollisionClient.OnRoundStart();
#endif
        }

        public void Release() => WallRules.Reset();

#if CLIENT
        public static void SessionUpdatePostfix(float deltaTime) => WallCollisionClient.Update(deltaTime);
#endif

        // Release fixture gates before the engine removes their bodies.
        public static void StructureRemovingPrefix(Structure __instance) => WallRules.Removed(__instance);

        public static void EndRoundPostfix() {
            WallRules.Reset();
#if CLIENT
            WallCollisionClient.OnRoundEnd();
#endif
        }

        // UpdateSections destroys and recreates wall bodies, even when the wall instance is unchanged.
        public static void SectionsPostfix(Structure __instance) => WallRules.Rebind(__instance);
    }
}
