using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Spawn {
    public static class SleepSpawnEditorPanel {
        public static void CreateEditingHUDPostfix(WayPoint __instance, GUIComponent __result) {
            if (__instance == null || __result == null || __instance.SpawnType != SpawnType.Human) { return; }
            GUILayoutGroup padded = __result.GetChild<GUILayoutGroup>();
            if (padded == null) { return; }

            var toggle = new GUITickBox(new RectTransform(new Vector2(1.0f, 0.2f), padded.RectTransform),
                "Усадить/уложить при появлении") {
                Selected = SpawnConfig.HasSleepTag(__instance),
                ToolTip = "Персонаж, появившийся в этой точке, будет немедленно посажен или уложен на ближайшую " +
                          "подходящую мебель (кровать, стул, скамья) в радиусе " + (int)SpawnConfig.SleepSearchRadius +
                          " ед. Добавляет точке тег " + SpawnConfig.SleepSpawnTag + "."
            };

            var status = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.2f), padded.RectTransform),
                "", font: GUIStyle.SmallFont) { CanBeFocused = false };

            toggle.OnSelected = box => {
                SetTag(__instance, box.Selected);
                Refresh(__instance, box.Selected, status);
                return true;
            };
            Refresh(__instance, toggle.Selected, status);

            padded.Recalculate();
            __result.RectTransform.Resize(new Point(__result.Rect.Width,
                (int)(padded.Children.Sum(c => c.Rect.Height + padded.AbsoluteSpacing) / padded.RectTransform.RelativeSize.Y)));
        }

        private static void Refresh(WayPoint waypoint, bool enabled, GUITextBlock status) {
            if (!enabled) {
                status.Text = "";
                return;
            }
            Item furniture = SpawnPlacement.FindSeat(waypoint.WorldPosition);
            if (furniture == null) {
                status.Text = "Подходящей мебели в радиусе не найдено";
                status.TextColor = GUIStyle.Red;
                return;
            }
            float distance = Vector2.Distance(furniture.WorldPosition, waypoint.WorldPosition);
            status.Text = $"Мебель: {furniture.Name} ({(int)distance} ед.)";
            status.TextColor = GUIStyle.Green;
        }

        private static void SetTag(WayPoint waypoint, bool enabled) {
            HashSet<Identifier> tags = waypoint.tags ??= new HashSet<Identifier>();
            if (enabled) { tags.Add(SpawnConfig.SleepSpawnTag); } else { tags.Remove(SpawnConfig.SleepSpawnTag); }
            SyncTagsTextBox(waypoint, tags);
        }

        private static void SyncTagsTextBox(WayPoint waypoint, HashSet<Identifier> tags) {
            GUIComponent hud = MapEntity.EditingHUD;
            if (hud == null || hud.UserData != waypoint) { return; }
            LocalizedString label = TextManager.Get("spawnpointtags");
            foreach (GUIComponent child in hud.GetAllChildren()) {
                if (child is not GUITextBlock block || block.Text != label) { continue; }
                if (block.GetChild<GUITextBox>() is { } box) { box.Text = string.Join(",", tags); }
                return;
            }
        }
    }
}
