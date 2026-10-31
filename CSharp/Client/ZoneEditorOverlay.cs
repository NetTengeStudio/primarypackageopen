using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Zones {
    public static class ZoneEditorOverlay {
        public static void Draw(SpriteBatch spriteBatch, Item item, Color color) {
            Camera cam = Screen.Selected?.Cam;
            if (cam == null || cam.Zoom <= 0.0f) { return; }
            Rectangle rect = item.WorldRect;
            var pos = new Vector2(rect.X, -rect.Y);
            var size = new Vector2(rect.Width, rect.Height);
            bool selected = item.IsSelected || item.IsHighlighted;
            float px = 1.0f / cam.Zoom;
            GUI.DrawRectangle(spriteBatch, pos, size, color * (selected ? 0.22f : 0.1f), isFilled: true);
            float thickness = Math.Max(1.0f, (selected ? 3.0f : 2.0f) * px);
            GUI.DrawRectangle(spriteBatch, pos, size, color * (selected ? 1.0f : 0.75f), thickness: thickness);
            if (!selected && rect.Width * cam.Zoom < 32.0f) { return; }
            GUIFont font = GUIStyle.SmallFont;
            LocalizedString name = item.Prefab.Name;
            Vector2 scale = Vector2.One * px;
            Vector2 labelPos = pos + new Vector2(6.0f, 4.0f) * px;
            font.DrawString(spriteBatch, name, labelPos + scale, Color.Black * 0.7f, 0.0f, Vector2.Zero, scale, SpriteEffects.None, 0.01f);
            font.DrawString(spriteBatch, name, labelPos, color * 0.9f, 0.0f, Vector2.Zero, scale, SpriteEffects.None, 0.0f);
        }
    }
}
