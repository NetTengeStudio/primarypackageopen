using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Core;
using PrimaryPackage.Fonts;

namespace PrimaryPackage.Thoughts {
    public sealed class ThoughtLayout {
        public FontHandle Font { get; private set; }
        public string Wrapped { get; private set; } = "";
        public Vector2 Size { get; private set; }
        public Vector2 Origin { get; private set; }

        private string sourceText = "";
        private float sourceMaxWidth;
        private float sourceGuiScale;
        private string[] slices = Array.Empty<string>();

        public static FontHandle ResolveFont() {
            GUIFont large = GUIStyle.LargeFont;
            if (large != null && large.HasValue && large.Size >= 18) {
                return new FontHandle(large);
            }
            return new FontHandle(GUIStyle.Font);
        }

        public void Build(string text, float maxWidth) {
            text ??= "";
            float guiScale = GUI.Scale;
            if (ReferenceEquals(sourceText, text) && sourceMaxWidth == maxWidth && sourceGuiScale == guiScale && Font.HasValue) {
                return;
            }

            sourceText = text;
            sourceMaxWidth = maxWidth;
            sourceGuiScale = guiScale;

            Font = ResolveFont();
            Wrapped = Font.HasValue ? Font.WrapText(text, maxWidth) : text;
            Size = Font.HasValue ? Font.MeasureString(Wrapped) : Vector2.Zero;

            Origin = new Vector2(
                MathF.Round(Size.X * 0.5f),
                MathF.Round(Size.Y * 0.5f)
            );

            int len = Wrapped.Length;
            if (slices.Length <= len) {
                slices = new string[Math.Max(len + 1, slices.Length * 2)];
            }
            for (int i = 0; i <= len; i++) {
                slices[i] = Wrapped.Substring(0, i);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string Visible(int count) {
            if (count <= 0) { return ""; }
            if (count >= Wrapped.Length) { return Wrapped; }
            return slices[count];
        }
    }

    public static class ThoughtRenderer {
        private const float MaxWidthPercent = 0.44f;
        private const float MaxWidthHeightRatio = 0.78f;
        private const float ShadowOffsetBase = 1.5f;

        private static readonly Color[] Tints = BuildTints();

        public static float WrapWidth() {
            float byWidth = GameMain.GraphicsWidth * MaxWidthPercent;
            float byHeight = GameMain.GraphicsHeight * MaxWidthHeightRatio;
            return MathF.Round(byWidth < byHeight ? byWidth : byHeight);
        }

        public static void Draw(SpriteBatch spriteBatch, ThoughtLayout layout, ThoughtCategory category,
                                in ThoughtFrame frame, float painSmooth, float severitySmooth) {
            if (frame.Alpha <= 0.004f || !layout.Font.HasValue) { return; }

            string visibleText = layout.Visible(frame.Solid);
            if (visibleText.Length == 0) { return; }

            int offsetX = 0;
            int offsetY = 0;
            if (severitySmooth > 0.10f) {
                float intensity = Scalar.Clamp01((severitySmooth - 0.10f) / 0.90f);
                float maxOffset = 1.6f * GUI.Scale;
                float amplitude = intensity * maxOffset;

                float time = ThoughtClock.Time;
                offsetX = (int)MathF.Round(Scalar.Noise(time * 15.5f) * amplitude);
                offsetY = (int)MathF.Round(Scalar.Noise(time * 13.0f + 23.0f) * (amplitude * 0.60f));
            }

            Vector2 origin = layout.Origin;

            Rectangle hotbar = HUDLayoutSettings.InventoryAreaLower;
            float bottom = hotbar.Height > 0 ? hotbar.Y : GameMain.GraphicsHeight * 0.86f;

            Vector2 center = new Vector2(
                MathF.Round(GameMain.GraphicsWidth * 0.5f + offsetX),
                MathF.Round(bottom - GameMain.GraphicsHeight * 0.075f - origin.Y + offsetY)
            );

            Color color = Tint(category, painSmooth);
            Color shadow = Color.Black;
            float alpha = frame.Alpha;

            float shadowShiftPx = MathF.Round(ShadowOffsetBase * GUI.Scale);
            Vector2 shadowShift = new Vector2(shadowShiftPx, shadowShiftPx);

            layout.Font.DrawString(spriteBatch, visibleText, center + shadowShift, shadow * (alpha * 0.75f),
                0.0f, origin, Vector2.One, SpriteEffects.None, 0.0f);

            layout.Font.DrawString(spriteBatch, visibleText, center, color * alpha,
                0.0f, origin, Vector2.One, SpriteEffects.None, 0.0f);
        }

        public static Color Tint(ThoughtCategory category, float pain) {
            if (Tints == null || Tints.Length == 0) { return Color.White; }
            int index = (int)category;
            Color baseTint = index >= 0 && index < Tints.Length ? Tints[index] : Tints[0];
            if (pain <= 0.001f) { return baseTint; }

            float calm = Scalar.Clamp01((90.0f - pain) / 90.0f);
            var hurt = new Color(255, (int)(18 + calm * 218), (int)(18 + calm * 210), 255);
            float mix = Scalar.Clamp01(pain / 70.0f);
            return Color.Lerp(baseTint, hurt, mix);
        }

        private static Color[] BuildTints() {
            var tints = new Color[Enum.GetValues(typeof(ThoughtCategory)).Length];
            for (int i = 0; i < tints.Length; i++) { tints[i] = new Color(238, 234, 226); }
            tints[(int)ThoughtCategory.Idle] = new Color(196, 202, 212);
            tints[(int)ThoughtCategory.Fear] = new Color(206, 216, 236);
            tints[(int)ThoughtCategory.Irony] = new Color(216, 214, 196);
            tints[(int)ThoughtCategory.Aimed] = new Color(228, 224, 232);
            tints[(int)ThoughtCategory.NearDeath] = new Color(196, 168, 168);
            tints[(int)ThoughtCategory.Will] = new Color(228, 230, 224);
            tints[(int)ThoughtCategory.Daze] = new Color(178, 196, 182);
            tints[(int)ThoughtCategory.Broken] = new Color(152, 184, 158);
            tints[(int)ThoughtCategory.Hungry] = new Color(214, 204, 162);
            tints[(int)ThoughtCategory.Starving] = new Color(206, 188, 140);
            tints[(int)ThoughtCategory.Fever] = new Color(236, 178, 124);
            tints[(int)ThoughtCategory.Choke] = new Color(158, 194, 228);
            tints[(int)ThoughtCategory.Wake] = new Color(212, 208, 208);
            tints[(int)ThoughtCategory.Chill] = new Color(186, 208, 228);
            tints[(int)ThoughtCategory.Freezing] = new Color(158, 194, 232);
            tints[(int)ThoughtCategory.Frostbite] = new Color(132, 176, 228);
            tints[(int)ThoughtCategory.Darkness] = new Color(168, 176, 196);
            return tints;
        }
    }
}
