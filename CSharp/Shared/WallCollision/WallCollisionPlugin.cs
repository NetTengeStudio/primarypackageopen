using System;
using System.Reflection;
using Barotrauma;
using HarmonyLib;

namespace PrimaryPackage.WallCollision {
    public sealed class WallCollisionPlugin : PrimaryPackage.Core.IModule {
        public void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(FarseerPhysics.Dynamics.World), "Step", new[] { typeof(float) }),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallContacts), nameof(WallContacts.StepPrefix)));
            WallMovementQueries.Attach(harmony);
            harmony.Patch(AccessTools.Method(typeof(Ragdoll), nameof(Ragdoll.OnLimbCollision)),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallContacts), nameof(WallContacts.CollisionPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Structure), "OnWallCollision"),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallContacts), nameof(WallContacts.CollisionPrefix)));
            WallCollisionCommand.Register();
            WallCollisionActivation.Register();
#if CLIENT
            WallCollisionPreparation.Register();
#endif
            PrimaryPackage.Core.ModuleHost.Guard("ec frame", () => harmony.Patch(
                AccessTools.Method(typeof(GameSession), nameof(GameSession.Update)),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallCollisionPlugin), nameof(SessionUpdatePostfix))));
            PrimaryPackage.Core.ModuleHost.Guard("ec load", () => {
                MethodInfo target = AccessTools.Method(typeof(Structure), "Load");
                if (target == null) { throw new MissingMethodException("Structure.Load"); }
                harmony.Patch(target,
                    postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallCollisionPlugin), nameof(LoadPostfix)));
            });
            PrimaryPackage.Core.ModuleHost.Guard("ec remove", () => {
                MethodInfo target = AccessTools.Method(typeof(Structure), nameof(Structure.Remove));
                if (target == null) { throw new MissingMethodException("Structure.Remove"); }
                harmony.Patch(target,
                    prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(WallCollisionPlugin), nameof(RemovePrefix)));
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

        public void Release() {
            WallRules.Reset();
            WallMovementQueries.Release();
        }

        public static void SessionUpdatePostfix(float deltaTime) {
            WallRules.Prune();
            if (WallContacts.Count == 0) { WallContacts.Release(); }
#if CLIENT
            WallCollisionClient.Update(deltaTime);
#endif
        }

        public static void LoadPostfix(Structure __result) => WallRules.RegisterPrepared(__result);

        public static void RemovePrefix(Structure __instance) => WallRules.UnregisterPrepared(__instance);

        public static void SectionsPostfix(Structure __instance) => WallRules.Rebind(__instance);
    }
}
