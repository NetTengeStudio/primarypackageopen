using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Inspect {
    public static class InspectRing {
        public const float InnerScale = 0.62f;

        private const int ProfileHeight = 32;
        private const float ProfileFeather = 5.0f;
        private const float FillGap = 0.6f;
        private const float SegmentsPerUnit = 1.5f;
        private const int MinSegments = 8;
        private const int MaxSegments = 192;

        private const string ProfileKey = "inspect/profile";
        private static Texture2D profile;

        private static Texture2D Profile {
            get {
                if (profile != null && !profile.IsDisposed) { return profile; }
                var data = new Color[ProfileHeight];
                for (int i = 0; i < ProfileHeight; i++) {
                    int edge = Math.Min(i, ProfileHeight - 1 - i);
                    data[i] = Color.White * MathHelper.Clamp(edge / ProfileFeather, 0.0f, 1.0f);
                }
                profile = new Texture2D(GameMain.Instance.GraphicsDevice, 1, ProfileHeight);
                profile.SetData(data);
                PrimaryPackage.Core.TextureStore.Adopt(ProfileKey, profile);
                return profile;
            }
        }

        public static void Dispose() {
            profile = null;
        }

        private static Vector2 OnCircle(Vector2 center, float radius, float angle) =>
            new Vector2(center.X + radius * (float)Math.Sin(angle), center.Y - radius * (float)Math.Cos(angle));

        public static void Draw(SpriteBatch spriteBatch, Vector2 center, float radius, float thickness,
                                float startAngle, float sweep, Color color, float depth = 0.0f) {
            if (sweep <= 0.0f || thickness <= 0.0f || radius <= 0.0f || color.A == 0) { return; }
            sweep = Math.Min(sweep, MathHelper.TwoPi);
            float full = MathHelper.Clamp(MinSegments + (radius + thickness * 2.0f) * SegmentsPerUnit, MinSegments, MaxSegments);
            int segments = Math.Max(2, (int)Math.Ceiling(sweep / MathHelper.TwoPi * full));
            float step = sweep / segments;
            float overlap = thickness * step * 0.5f + 0.25f;
            float mid = radius - thickness * 0.5f;
            Texture2D texture = Profile;
            var origin = new Vector2(0.0f, ProfileHeight * 0.5f);
            var scale = new Vector2(0.0f, thickness / ProfileHeight);
            for (int i = 0; i < segments; i++) {
                float angle = startAngle + step * i;
                Vector2 from = OnCircle(center, mid, angle);
                Vector2 delta = OnCircle(center, mid, angle + step) - from;
                float length = delta.Length();
                if (length <= 0.0f) { continue; }
                scale.X = length + overlap;
                spriteBatch.Draw(texture, from, null, color, (float)Math.Atan2(delta.Y, delta.X), origin,
                    scale, SpriteEffects.None, depth);
            }
        }

        public static void DrawGauge(SpriteBatch spriteBatch, Vector2 center, float radius, float thickness,
                                     float fill, Color track, Color fillColor, float depth = 0.0f) {
            Draw(spriteBatch, center, radius, thickness, 0.0f, MathHelper.TwoPi, track, depth);
            Draw(spriteBatch, center, radius * InnerScale, thickness, 0.0f, MathHelper.TwoPi, track, depth);
            float gap = thickness * FillGap;
            float outer = radius - thickness - gap;
            float inner = radius * InnerScale + gap;
            if (outer > inner) {
                Draw(spriteBatch, center, outer, outer - inner, 0.0f, MathHelper.TwoPi * fill, fillColor, depth);
            }
        }
    }
}
