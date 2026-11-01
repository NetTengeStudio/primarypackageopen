using System;
using System.Globalization;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Aes;

namespace PrimaryPackage.Cold {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public partial class ColdSourceComponent : IDrawableComponent {
        private static readonly Color ColdColor = new Color(120, 190, 235);
        private static readonly Color HeatColor = new Color(235, 165, 105);

        public Vector2 DrawSize => new Vector2(192.0f, 192.0f);

        partial void UpdateProjSpecific(float deltaTime) {}

        public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null) {
            if (!editing || Screen.Selected != GameMain.SubEditorScreen) { return; }
            if (!AesAuxSystems.Visible(AesAuxSystems.Kind.Cold)) { return; }
            Camera cam = Screen.Selected?.Cam;
            if (cam == null || cam.Zoom <= 0.0f) { return; }

            Color color = TargetTemperature < ColdConfig.BaseTemperature ? ColdColor : HeatColor;
            bool selected = item.IsSelected || item.IsHighlighted;
            float px = 1.0f / cam.Zoom;
            float thickness = Math.Max(1.0f, (selected ? 3.0f : 2.0f) * px);
            float radius = 24.0f;
            Vector2 center = new Vector2(item.WorldPosition.X, -item.WorldPosition.Y);

            GUI.DrawRectangle(spriteBatch, center - new Vector2(radius, radius), new Vector2(radius * 2.0f, radius * 2.0f),
                color * (selected ? 0.24f : 0.12f), isFilled: true);
            GUI.DrawRectangle(spriteBatch, center - new Vector2(radius, radius), new Vector2(radius * 2.0f, radius * 2.0f),
                color * (selected ? 1.0f : 0.75f), thickness: thickness);

            Hull hull = ColdHull ?? item.CurrentHull;
            if (hull != null) {
                Rectangle r = hull.WorldRect;
                var pos = new Vector2(r.X, -r.Y);
                var size = new Vector2(r.Width, r.Height);
                GUI.DrawRectangle(spriteBatch, pos, size, color * (selected ? 0.14f : 0.06f), isFilled: true);
                GUI.DrawRectangle(spriteBatch, pos, size, color * (selected ? 0.8f : 0.45f), thickness: thickness);
                var to = new Vector2(r.X + r.Width * 0.5f, -(r.Y - r.Height * 0.5f));
                GUI.DrawLine(spriteBatch, center, to, color * 0.7f, width: Math.Max(1.0f, 2.0f * px));
            }

            if (!selected && radius * 2.0f * cam.Zoom < 24.0f) { return; }
            GUIFont font = GUIStyle.SmallFont;
            if (font == null || !font.HasValue) { return; }
            string label = (TargetTemperature < ColdConfig.BaseTemperature ? "холод " : "тепло ") +
                           TargetTemperature.ToString("0.#", CultureInfo.InvariantCulture) + "°, сила " +
                           Rate.ToString("0.###", CultureInfo.InvariantCulture) +
                           (Enabled ? "" : " (выкл)");
            Vector2 scale = Vector2.One * px;
            Vector2 labelPos = center + new Vector2(radius + 6.0f * px, -radius);
            font.DrawString(spriteBatch, label, labelPos + scale, Color.Black * 0.7f, 0.0f, Vector2.Zero, scale, SpriteEffects.None, 0.01f);
            font.DrawString(spriteBatch, label, labelPos, color * 0.95f, 0.0f, Vector2.Zero, scale, SpriteEffects.None, 0.0f);
        }
    }
}
