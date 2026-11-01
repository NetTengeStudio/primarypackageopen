using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Barotrauma;
namespace PrimaryPackage.CrewManifest {
    [HarmonyPatch(typeof(CrewManager), nameof(CrewManager.AddCharacterToCrewList))]
    internal static class CrewListIconPatch {
        [HarmonyPostfix]
        public static void Postfix(Character character, GUIComponent __result) {
            if (__result == null || character?.Info == null) { return; }
            if (!ManifestLogic.TryGetOriginalJobPrefab(character.Info, out var originalPrefab) || originalPrefab.Icon == null) { return; }
            if (!(__result.GetAllChildren().FirstOrDefault(c => (c.UserData as string) == "job") is GUIImage jobIconBackground)) { return; }
            var badge = new GUIImage(
                new RectTransform(new Vector2(0.55f), jobIconBackground.RectTransform, anchor: Anchor.TopLeft) { RelativeOffset = new Vector2(-0.12f, -0.12f) },
                originalPrefab.Icon, scaleToFit: true) {
                CanBeFocused = false,
                Color = originalPrefab.UIColor,
                HoverColor = originalPrefab.UIColor,
                PressedColor = originalPrefab.UIColor,
                SelectedColor = originalPrefab.UIColor
            };
            badge.RectTransform.SetAsFirstChild();
        }
    }
}
