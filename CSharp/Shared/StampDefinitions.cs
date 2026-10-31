using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Microsoft.Xna.Framework;
namespace PrimaryPackage.Documents {
    public static class StampPlacement {
        public const float RotationRange = 0.12f;
        private const float ClusterLeft = 110f;
        private const float ClusterRight = 232f;
        private const float SignatureLineY = 306f;
        private const float ClusterBottom = SignatureLineY - 3f;
        private const float MaxClusterHeight = 90f;
        private const float TextStampGap = 8f;
        private const float StampWidth = 64f;
        private const float StampHeight = 29f;
        private const float StampHalfWidth = StampWidth / 2f;
        private const float StampHalfHeight = StampHeight / 2f;
        private const float MinNormalizedSeparation = 0.62f;
        private const int MaxPlacementAttempts = 40;
        public static Vector2 FindPlacement(IReadOnlyList<DocumentMarkComponent.StampEntry> existing,
            Random random, float occupiedTextBottom) {
            (float minX, float maxX, float minY, float maxY) = CenterBounds(occupiedTextBottom);
            if (existing == null || existing.Count == 0) { return new Vector2(maxX, maxY); }
            Vector2 best = RandomCenter(random, minX, maxX, minY, maxY);
            float bestSeparation = MinSeparationFrom(best, existing);
            if (bestSeparation >= MinNormalizedSeparation) { return best; }
            for (int attempt = 0; attempt < MaxPlacementAttempts; attempt++) {
                Vector2 candidate = RandomCenter(random, minX, maxX, minY, maxY);
                float separation = MinSeparationFrom(candidate, existing);
                if (separation >= MinNormalizedSeparation) { return candidate; }
                if (separation > bestSeparation) { best = candidate; bestSeparation = separation; }
            }
            return best;
        }
        public static Vector2 ClampOffset(Vector2 center) {
            (float minX, float maxX, float minY, float maxY) = CenterBounds(0f);
            return new Vector2(
                MathHelper.Clamp(center.X, Math.Min(minX, maxX), Math.Max(minX, maxX)),
                MathHelper.Clamp(center.Y, Math.Min(minY, maxY), Math.Max(minY, maxY)));
        }
        private static (float minX, float maxX, float minY, float maxY) CenterBounds(float occupiedTextBottom) {
            float cos = Math.Abs((float)Math.Cos(RotationRange));
            float sin = Math.Abs((float)Math.Sin(RotationRange));
            float halfWidth = StampHalfWidth * cos + StampHalfHeight * sin;
            float halfHeight = StampHalfWidth * sin + StampHalfHeight * cos;
            float top = Math.Max(occupiedTextBottom + TextStampGap, ClusterBottom - MaxClusterHeight);
            return (ClusterLeft + halfWidth, ClusterRight - halfWidth, top + halfHeight, ClusterBottom - halfHeight);
        }
        private static Vector2 RandomCenter(Random random, float minX, float maxX, float minY, float maxY) {
            float x = MathHelper.Lerp(Math.Min(minX, maxX), Math.Max(minX, maxX), (float)random.NextDouble());
            float y = MathHelper.Lerp(Math.Min(minY, maxY), Math.Max(minY, maxY), (float)random.NextDouble());
            return new Vector2(x, y);
        }
        private static float MinSeparationFrom(Vector2 candidate, IReadOnlyList<DocumentMarkComponent.StampEntry> existing) {
            if (existing == null || existing.Count == 0) { return float.MaxValue; }
            float min = float.MaxValue;
            foreach (DocumentMarkComponent.StampEntry stamp in existing) {
                float dx = (candidate.X - stamp.Offset.X) / StampWidth;
                float dy = (candidate.Y - stamp.Offset.Y) / StampHeight;
                float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                if (distance < min) { min = distance; }
            }
            return min;
        }
    }
    public static class StampDefinitions {
        public static readonly Identifier CoalitionStamp = "coalition".ToIdentifier();
        public static readonly Identifier ApprovedStamp = "approved".ToIdentifier();
        public static readonly Identifier DeniedStamp = "denied".ToIdentifier();
        public static readonly Identifier EngineerStamp = "engineer".ToIdentifier();
        public static readonly Identifier MedicalDoctorStamp = "medicaldoctor".ToIdentifier();
        public static readonly Identifier MechanicStamp = "mechanic".ToIdentifier();
        public static readonly Identifier SecurityOfficerStamp = "securityofficer".ToIdentifier();
        public static readonly Identifier StampItemId = "primarypkg_stamp".ToIdentifier();
        public const string ApplyNetId = "primarypkg_stamp_apply";
        public const string MarkComponentTypeName = "DocumentMarkComponent";
        public const string StampTag = "primarypkg_stamp";
        public static bool IsStampItem(Item item) => item != null && item.HasTag(StampTag);
        public static bool DetectSubsystemEnabled() =>
            ReflectionUtils.GetDerivedNonAbstract<ItemComponent>().Any(type => type.Name == MarkComponentTypeName);
        public static bool HoldableUsePrefix(Holdable __instance, ref bool __result) {
            if (IsStampItem(__instance.Item)) {
                __result = true;
                return false;
            }
            return true;
        }
        public static Identifier GetStampIdFromItem(Item stampItem) =>
            stampItem.Prefab.Identifier == "primarypkg_stamp_approved" ? ApprovedStamp :
            stampItem.Prefab.Identifier == "primarypkg_stamp_denied" ? DeniedStamp :
            stampItem.Prefab.Identifier == "primarypkg_stamp_engineer" ? EngineerStamp :
            stampItem.Prefab.Identifier == "primarypkg_stamp_medicaldoctor" ? MedicalDoctorStamp :
            stampItem.Prefab.Identifier == "primarypkg_stamp_mechanic" ? MechanicStamp :
            stampItem.Prefab.Identifier == "primarypkg_stamp_securityofficer" ? SecurityOfficerStamp :
            CoalitionStamp;
    }
}
