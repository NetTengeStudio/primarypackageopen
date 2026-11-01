using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Aes;
using PrimaryPackage.Zones;

namespace PrimaryPackage.Cold {
    public partial class ColdZoneComponent : IDrawableComponent {
        private static readonly Color zoneColor = new Color(120, 190, 235);

        public Vector2 DrawSize => new Vector2(item.Rect.Width, item.Rect.Height);

        partial void UpdateProjSpecific(float deltaTime) {}

        public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null) {
            if (!editing || Screen.Selected != GameMain.SubEditorScreen) { return; }
            if (!AesAuxSystems.Visible(AesAuxSystems.Kind.Cold)) { return; }
            ZoneEditorOverlay.Draw(spriteBatch, item, zoneColor);
        }
    }
}
