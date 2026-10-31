using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace PrimaryPackage.Cold {
    public static class ColdHullPicker {
        private static readonly Color HoverColor = new Color(120, 190, 235);
        private static readonly Color LinkColor = new Color(180, 220, 255);

        private static ColdSourceComponent target;
        private static Hull hovered;
        private static bool armed;

        public static bool Active { get; private set; }

        public static void Begin(Item item) {
            target = item?.GetComponent<ColdSourceComponent>();
            if (target == null) { return; }
            hovered = null;
            armed = false;
            Active = true;
        }

        private static void End() {
            Active = false;
            target = null;
            hovered = null;
        }

        public static void Update() {
            if (!Active) { return; }
            if (!SubEditorScreen.IsSubEditor() || target?.Item == null || target.Item.Removed) { End(); return; }
            Camera cam = Screen.Selected?.Cam;
            if (cam == null) { End(); return; }

            if (!PlayerInput.PrimaryMouseButtonHeld()) { armed = true; }
            if (!PrimaryPackage.Core.PlayView.Typing && PlayerInput.KeyHit(Keys.Escape)) { End(); return; }
            if (PlayerInput.SecondaryMouseButtonClicked()) { End(); return; }

            hovered = Hull.FindHull(cam.ScreenToWorld(PlayerInput.MousePosition));
            if (!armed || GUI.MouseOn != null || !PlayerInput.PrimaryMouseButtonClicked()) { return; }
            target.Rebind(hovered);
            End();
        }

        public static void Draw(SpriteBatch spriteBatch) {
            if (!Active) { return; }
            Camera cam = Screen.Selected?.Cam;
            if (cam == null || cam.Zoom <= 0.0f) { return; }
            float px = 1.0f / cam.Zoom;
            float thickness = Math.Max(1.0f, 3.0f * px);

            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, transformMatrix: cam.Transform);
            if (hovered != null) {
                Rectangle r = hovered.WorldRect;
                var pos = new Vector2(r.X, -r.Y);
                var size = new Vector2(r.Width, r.Height);
                GUI.DrawRectangle(spriteBatch, pos, size, HoverColor * 0.18f, isFilled: true);
                GUI.DrawRectangle(spriteBatch, pos, size, HoverColor, thickness: thickness);
                if (target?.Item != null) {
                    Vector2 from = target.Item.WorldPosition;
                    Vector2 to = new Vector2(r.X + r.Width * 0.5f, r.Y - r.Height * 0.5f);
                    GUI.DrawLine(spriteBatch, new Vector2(from.X, -from.Y), new Vector2(to.X, -to.Y),
                        LinkColor, width: Math.Max(1.0f, 2.0f * px));
                }
            }
            spriteBatch.End();

            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            GUIFont font = GUIStyle.SubHeadingFont;
            if (font != null && font.HasValue) {
                Vector2 size = font.MeasureString(Hint);
                font.DrawString(spriteBatch, Hint,
                    new Vector2(GameMain.GraphicsWidth * 0.5f, GameMain.GraphicsHeight * 0.12f),
                    HoverColor, 0.0f, size * 0.5f, 1.0f, SpriteEffects.None, 0.0f);
            }
            spriteBatch.End();
        }

        private const string Hint = "Выберите отсек для источника холода: ЛКМ — привязать, ПКМ или Esc — отмена";
    }
}
