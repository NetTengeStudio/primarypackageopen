using System;
using System.Reflection;
using Barotrauma;
using HarmonyLib;

namespace PrimaryPackage.WallCollision {
    public sealed class WallCollisionPlugin : PrimaryPackage.Core.IModule, PrimaryPackage.Core.IFrameModule {
        public void Attach(Harmony harmony) {
            WallCollisionCommand.Register();
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

        public void FrameUpdate(float deltaTime) {
            WallRules.Prune();
            if (WallContacts.Count == 0) { WallContacts.Release(); }
#if CLIENT
            WallCollisionClient.Update(deltaTime);
#endif
        }

        public static void SectionsPostfix(Structure __instance) => WallRules.Rebind(__instance);
    }
}
