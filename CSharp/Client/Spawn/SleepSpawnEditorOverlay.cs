using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Spawn {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public static class SleepSpawnEditorOverlay {
        public static void WayPointDrawPostfix(WayPoint __instance, SpriteBatch spriteBatch, bool editing) {
            if (!editing || __instance.SpawnType != SpawnType.Human) { return; }
            if (!__instance.IsHighlighted && !__instance.IsSelected) { return; }
            if (!SpawnConfig.HasSleepTag(__instance)) { return; }
            Camera cam = Screen.Selected?.Cam;
            if (cam == null || cam.Zoom <= 0.0f) { return; }
            Item furniture = SpawnPlacement.FindSeat(__instance.WorldPosition);
            if (furniture == null) { return; }

            float px = 1.0f / cam.Zoom;
            Rectangle rect = furniture.WorldRect;
            Vector2 pos = new Vector2(rect.X, -rect.Y);
            Vector2 size = new Vector2(rect.Width, rect.Height);
            GUI.DrawRectangle(spriteBatch, pos, size, Color.Cyan * 0.25f, isFilled: true);
            GUI.DrawRectangle(spriteBatch, pos, size, Color.Cyan, thickness: 2.0f * px);
            GUI.DrawLine(spriteBatch, __instance.DrawPosition, furniture.DrawPosition, Color.Cyan * 0.6f, width: 1.5f * px);
        }
    }
}
