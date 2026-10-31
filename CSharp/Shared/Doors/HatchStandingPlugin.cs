using System;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Doors {
    public sealed class HatchStandingPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            PrimaryPackage.Core.ModuleHost.Guard("Character.CanInteractWith", () => harmony.Patch(
                AccessTools.Method(typeof(Character), nameof(Character.CanInteractWith),
                    new[] { typeof(Item), typeof(float).MakeByRefType(), typeof(bool) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HatchStandingPlugin), nameof(CanInteractWithPrefix)))));
        }

        public void Release() { }

        public static bool CanInteractWithPrefix(Character __instance, Item item, ref float distanceToItem, ref bool __result) {
            if (!StandsOnHatch(__instance, item)) { return true; }
            distanceToItem = 0f;
            __result = true;
            return false;
        }

        public static bool StandsOnHatch(Character character, Item item) {
            if (character == null || item == null || character.IsDead || character.Removed || item.Removed) { return false; }
            if (item.GetComponent<Door>() is not { IsHorizontal: true, IsBroken: false }) { return false; }
            PhysicsBody body = character.AnimController?.Collider;
            if (body == null) { return false; }
            Vector2 offset = body.SimPosition - item.SimPosition;
            Rectangle rect = item.Rect;
            return HatchReach.Covers(
                offset.X,
                offset.Y,
                ConvertUnits.ToSimUnits(rect.Width * 0.5f),
                ConvertUnits.ToSimUnits(rect.Height * 0.5f),
                body.GetMaxExtent());
        }
    }

    public static class HatchReach {
        public static bool Covers(float dx, float dy, float halfWidth, float halfHeight, float reach) =>
            Math.Abs(dx) <= halfWidth && dy >= 0f && dy <= halfHeight + reach;
    }
}
