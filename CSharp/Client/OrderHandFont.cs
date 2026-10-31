using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Fonts;
namespace PrimaryPackage.Economy {
    public static class OrderHandFont {
        private const string FontName = "Caveat";
        private const uint AtlasSize = 48;
        public static ScalableFont Font => FontRegistry.Get(FontName.ToIdentifier(), AtlasSize, dynamicLoading: false);
        public static Vector2 Measure(string text, float worldPx) {
            ScalableFont f = Font;
            if (f == null || string.IsNullOrEmpty(text)) { return Vector2.Zero; }
            return f.MeasureString(text) * (worldPx / f.Size);
        }
        public static void DrawCentered(SpriteBatch sb, string text, Vector2 pivot, float lineCenterY,
            float worldPx, float angle, Color color, float depth) {
            ScalableFont f = Font;
            if (f == null || string.IsNullOrEmpty(text)) { return; }
            float scale = worldPx / f.Size;
            Vector2 m = f.MeasureString(text) * scale;
            Vector2 topLeft = new Vector2(pivot.X - m.X / 2f, lineCenterY - m.Y / 2f);
            Vector2 rotated = RotateAround(topLeft, pivot, angle);
            f.DrawString(sb, text, rotated, color, angle, Vector2.Zero, new Vector2(scale, scale),
                SpriteEffects.None, depth);
        }
        public static Vector2 RotateAround(Vector2 p, Vector2 c, float a) {
            float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
            Vector2 d = p - c;
            return new Vector2(c.X + d.X * cos - d.Y * sin, c.Y + d.X * sin + d.Y * cos);
        }
    }
}
