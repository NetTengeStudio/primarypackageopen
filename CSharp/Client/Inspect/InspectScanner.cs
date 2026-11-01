using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Inspect {
    public static class InspectScanner {
        private static InspectTarget hovered;

        public static InspectTarget Hovered => hovered;

        public static void Update() {
            hovered = default;
            Character character = Character.Controlled;
            if (character == null || Screen.Selected != GameMain.GameScreen || !character.CanInteract) { return; }
            if (InspectRegistry.Count == 0) { return; }
            Camera cam = GameMain.GameScreen.Cam;
            if (cam == null) { return; }
            Vector2 mouseWorld = cam.ScreenToWorld(PlayerInput.LatestMousePosition);
            float bestDepth = float.MaxValue;
            for (int i = 0; i < InspectRegistry.Count; i++) {
                MapEntity entity = InspectRegistry.EntityAt(i);
                InspectData data = InspectRegistry.DataAt(i);
                if (entity.Removed || !data.PromptEnabled || InspectSeen.Blocks(data, entity)) { continue; }
                float range = data.PromptRange;
                if (RectDistanceSquared(character.WorldPosition, entity.WorldRect) > range * range) { continue; }
                if (!entity.IsMouseOn(mouseWorld)) { continue; }
                if (entity.SpriteDepth >= bestDepth) { continue; }
                bestDepth = entity.SpriteDepth;
                hovered = new InspectTarget(data, entity, data.Caches);
            }
        }

        private static float RectDistanceSquared(Vector2 position, Rectangle rect) {
            float dx = MathHelper.Max(MathHelper.Max(rect.X - position.X, position.X - (rect.X + rect.Width)), 0.0f);
            float dy = MathHelper.Max(MathHelper.Max(position.Y - rect.Y, rect.Y - rect.Height - position.Y), 0.0f);
            return dx * dx + dy * dy;
        }
    }
}
