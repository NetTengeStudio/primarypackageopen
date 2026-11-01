using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Barotrauma;
namespace PrimaryPackage.CrewManifest {
    [HarmonyPatch(typeof(CharacterInfo), nameof(CharacterInfo.DrawJobIcon))]
    internal static class JobIconDrawPatch {
        [HarmonyPrefix]
        public static void Prefix(CharacterInfo __instance, SpriteBatch spriteBatch, Rectangle area) {
            if (__instance == null) { return; }
            if (!ManifestLogic.TryGetOriginalJobPrefab(__instance, out var originalPrefab)) { return; }
            var icon = originalPrefab.Icon;
            if (icon == null || icon.SourceRect.Width <= 0 || icon.SourceRect.Height <= 0) { return; }
            float mainScale = Math.Min(area.Width / (float)icon.SourceRect.Width, area.Height / (float)icon.SourceRect.Height);
            var badgeCenter = new Vector2(area.Left + area.Width * 0.28f, area.Top + area.Height * 0.28f);
            icon.Draw(spriteBatch, badgeCenter, originalPrefab.UIColor, rotate: 0f, scale: mainScale * 0.55f);
        }
    }
}
