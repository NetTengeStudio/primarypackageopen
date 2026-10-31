using System;
using System.Reflection;
using System.Collections.Generic;
using System.Reflection.Emit;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using Barotrauma;
using HarmonyLib;

namespace PrimaryPackage.WallCollision {
    public sealed class WallCollisionPlugin : PrimaryPackage.Core.IModule {
        public void Attach(Harmony harmony) {
            PrimaryPackage.Core.ModuleHost.Guard("ec floor raycast", () => {
                // Avoid altering all physics raycasts: replace only the call site
                // inside the normal character's floor probe.
                MethodInfo getFloor = AccessTools.Method(typeof(Ragdoll), "GetFloorY");
                if (getFloor == null) { throw new MissingMethodException("Ragdoll.GetFloorY"); }
                harmony.Patch(getFloor, transpiler:
                    PrimaryPackage.Core.ModuleHost.Bind(typeof(WallCollisionPlugin), nameof(FloorRaycastTranspiler)));
            });
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

        public static IEnumerable<CodeInstruction> FloorRaycastTranspiler(IEnumerable<CodeInstruction> instructions) {
            MethodInfo raycast = AccessTools.Method(typeof(World), nameof(World.RayCast),
                new[] { typeof(Func<Fixture, Vector2, Vector2, float, float>), typeof(Vector2), typeof(Vector2), typeof(Category) });
            MethodInfo replacement = AccessTools.Method(typeof(WallCollisionPlugin), nameof(CastFloorRay));
            bool replaced = false;
            foreach (CodeInstruction instruction in instructions) {
                if (instruction.Calls(raycast)) {
                    if (replaced) { throw new InvalidOperationException("Ragdoll.GetFloorY has multiple raycasts"); }
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced = true;
                }
                yield return instruction;
            }
            if (!replaced) { throw new MissingMethodException("Ragdoll.GetFloorY World.RayCast call"); }
        }

        // Signature includes the World instance first, matching the callvirt stack.
        public static void CastFloorRay(World world,
            Func<Fixture, Vector2, Vector2, float, float> callback,
            Vector2 start, Vector2 end, Category category) {
            if (WallContacts.Count == 0) {
                world.RayCast(callback, start, end, category);
                return;
            }
            world.RayCast((fixture, point, normal, fraction) =>
                WallContacts.IgnoreFloorFixture(fixture, start, end)
                    ? -1f : callback(fixture, point, normal, fraction),
                start, end, category);
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
