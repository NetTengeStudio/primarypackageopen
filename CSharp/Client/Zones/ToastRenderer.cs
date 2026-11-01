using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Fonts;

namespace PrimaryPackage.Zones {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public sealed class ToastLayout {
        public FontHandle Font;
        public string Wrapped = "";
        public Vector2 UnscaledSize;
        public float DrawScale = 1.0f;

        private string sourceText;
        private float sourceScale;
        private string sourceFont;
        private float sourceMaxWidth;
        private float sourceGuiScale;
        private string visibleCache = "";
        private int visibleCount = -1;

        public bool Matches(in ToastSpec s, float maxWidth, float guiScale) =>
            ReferenceEquals(sourceText, s.Text) && sourceScale == s.Scale && sourceFont == s.FontName &&
            sourceMaxWidth == maxWidth && sourceGuiScale == guiScale;

        public void Rebuild(in ToastSpec s, float maxWidth, float guiScale) {
            sourceText = s.Text;
            sourceScale = s.Scale;
            sourceFont = s.FontName;
            sourceMaxWidth = maxWidth;
            sourceGuiScale = guiScale;
            visibleCount = -1;
            visibleCache = "";
            ToastRenderer.ChooseFont(s, out FontHandle font, out float drawScale);
            Font = font;
            DrawScale = drawScale;
            string text = s.Text ?? "";
            Wrapped = drawScale > 0.0f ? font.WrapText(text, maxWidth / drawScale) : text;
            UnscaledSize = font.MeasureString(Wrapped);
        }

        public string Visible(int chars) {
            if (chars >= Wrapped.Length) { return Wrapped; }
            if (chars <= 0) { return ""; }
            if (chars != visibleCount) {
                visibleCount = chars;
                visibleCache = Wrapped.Substring(0, chars);
            }
            return visibleCache;
        }
    }

    public static class ToastRenderer {
        private const float ShadowDepth = 0.3f;
        private const float OutlineDepth = 0.2f;
        private const float TextDepth = 0.1f;

        private static readonly Vector2[] blurRing = {
            new Vector2(1.0f, 0.0f), new Vector2(0.7071f, 0.7071f), new Vector2(0.0f, 1.0f), new Vector2(-0.7071f, 0.7071f),
            new Vector2(-1.0f, 0.0f), new Vector2(-0.7071f, -0.7071f), new Vector2(0.0f, -1.0f), new Vector2(0.7071f, -0.7071f)
        };
        private static readonly Vector2[] outlineDirs = {
            new Vector2(1.0f, 0.0f), new Vector2(-1.0f, 0.0f), new Vector2(0.0f, 1.0f), new Vector2(0.0f, -1.0f)
        };
        private static readonly Dictionary<GUIFont, (float guiScale, float height)> fontHeights = new Dictionary<GUIFont, (float, float)>();

        private static float FontHeight(GUIFont font) {
            float guiScale = GUI.Scale;
            if (!fontHeights.TryGetValue(font, out var cached) || cached.guiScale != guiScale) {
                cached = (guiScale, font.MeasureString("Ayg").Y);
                fontHeights[font] = cached;
            }
            return cached.height;
        }

        internal static void ChooseFont(in ToastSpec s, out FontHandle font, out float drawScale) {
            FontRef reference = FontRef.Parse(s.FontName);
            if (reference.IsFile) {
                float desired = Math.Max(GUIStyle.Font.Size * s.Scale, 1.0f);
                if (FontResolver.TryFile(reference.Name, desired, out font, out drawScale)) { return; }
            }
            GUIFont styleFont = FontResolver.Style(reference.Name);
            font = new FontHandle(styleFont);
            drawScale = s.Scale;
            if (!ReferenceEquals(styleFont, GUIStyle.Font) || s.Scale <= 1.1f) { return; }
            float hBase = FontHeight(styleFont);
            GUIFont large = GUIStyle.LargeFont;
            float hLarge = FontHeight(large);
            if (hLarge <= hBase * 1.05f || hBase <= 0.0f || hLarge <= 0.0f) { return; }
            float reduced = s.Scale * hBase / hLarge;
            if (reduced < drawScale) {
                font = new FontHandle(large);
                drawScale = reduced;
            }
        }

        public static void EnsureLayout(ToastLayout layout, in ToastSpec spec) {
            float maxWidth = GameMain.GraphicsWidth * spec.MaxWidthPercent * 0.01f;
            if (layout.Matches(spec, maxWidth, GUI.Scale)) { return; }
            layout.Rebuild(spec, maxWidth, GUI.Scale);
        }

        public static void EnsureLayout(ToastLayout layout, in ToastSpec spec, float maxWidth) {
            if (layout.Matches(spec, maxWidth, GUI.Scale)) { return; }
            layout.Rebuild(spec, maxWidth, GUI.Scale);
        }

        public static void DrawBox(SpriteBatch sb, Vector2 pos, Vector2 size, Color fill, Color edge, float depth, float unit) {
            Texture2D tex = GUI.WhiteTexture;
            if (tex == null) { return; }
            sb.Draw(tex, pos, null, fill, 0.0f, Vector2.Zero, size, SpriteEffects.None, depth);
            if (edge.A == 0) { return; }
            var horizontal = new Vector2(size.X, unit);
            var vertical = new Vector2(unit, size.Y);
            sb.Draw(tex, pos, null, edge, 0.0f, Vector2.Zero, horizontal, SpriteEffects.None, depth);
            sb.Draw(tex, new Vector2(pos.X, pos.Y + size.Y - unit), null, edge, 0.0f, Vector2.Zero, horizontal, SpriteEffects.None, depth);
            sb.Draw(tex, pos, null, edge, 0.0f, Vector2.Zero, vertical, SpriteEffects.None, depth);
            sb.Draw(tex, new Vector2(pos.X + size.X - unit, pos.Y), null, edge, 0.0f, Vector2.Zero, vertical, SpriteEffects.None, depth);
        }

        public static void Render(SpriteBatch sb, ToastLayout l, in ToastSpec s, in ToastState st, Vector2 anchor,
                                  float extraScale = 1.0f, float depth = -1.0f) {
            if (st.Alpha <= 0.001f || !l.Font.HasValue) { return; }
            bool custom = depth >= 0.0f;
            float textDepth = custom ? depth : TextDepth;
            float outlineDepth = custom ? depth + 0.001f : OutlineDepth;
            float shadowDepth = custom ? depth + 0.002f : ShadowDepth;
            float backDepth = custom ? depth + 0.003f : 0.0f;
            string txt = s.Typewriter ? l.Visible(st.VisibleChars) : l.Wrapped;
            float drawScale = l.DrawScale * extraScale;
            Vector2 blockSize = l.UnscaledSize * drawScale;
            Vector2 center = new Vector2(anchor.X, anchor.Y - blockSize.Y * 0.5f) + st.Offset * extraScale;
            Vector2 origin = l.UnscaledSize * 0.5f;
            Vector2 scaleVec = Vector2.One * (drawScale * st.Scale);

            if (s.Background && s.BackgroundAlpha > 0.0f) {
                Vector2 pad = Vector2.One * (s.BackgroundPadding * extraScale);
                DrawBox(sb, center - blockSize * 0.5f - pad, blockSize + pad * 2.0f,
                    s.BackgroundColor * (s.BackgroundAlpha * st.Alpha),
                    Color.White * (0.2f * s.BackgroundAlpha * st.Alpha), backDepth, extraScale);
            }
            if (txt.Length == 0) { return; }

            if (s.Shadow && s.ShadowAlpha > 0.0f) {
                Vector2 shadowPos = center + s.ShadowOffset * extraScale;
                if (s.ShadowBlur > 0.1f) {
                    Color ringColor = s.ShadowColor * (s.ShadowAlpha * st.Alpha * 0.22f);
                    l.Font.DrawString(sb, txt, shadowPos, s.ShadowColor * (s.ShadowAlpha * st.Alpha * 0.35f),
                        0.0f, origin, scaleVec, SpriteEffects.None, shadowDepth);
                    foreach (Vector2 dir in blurRing) {
                        l.Font.DrawString(sb, txt, shadowPos + dir * (s.ShadowBlur * extraScale), ringColor,
                            0.0f, origin, scaleVec, SpriteEffects.None, shadowDepth);
                    }
                } else {
                    l.Font.DrawString(sb, txt, shadowPos, s.ShadowColor * (s.ShadowAlpha * st.Alpha),
                        0.0f, origin, scaleVec, SpriteEffects.None, shadowDepth);
                }
            }

            if (s.Outline) {
                float w = Math.Max(1.0f, drawScale);
                Color oc = s.OutlineColor * st.Alpha;
                foreach (Vector2 dir in outlineDirs) {
                    l.Font.DrawString(sb, txt, center + dir * w, oc, 0.0f, origin, scaleVec, SpriteEffects.None, outlineDepth);
                }
            }

            l.Font.DrawString(sb, txt, center, s.TextColor * st.Alpha, 0.0f, origin, scaleVec, SpriteEffects.None, textDepth);
        }
    }
}
