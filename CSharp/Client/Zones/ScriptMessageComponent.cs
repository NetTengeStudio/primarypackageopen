using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Inspect;

namespace PrimaryPackage.Zones {
    public partial class ScriptMessageComponent : IDrawableComponent, ITypewriterPreview {
        private static readonly Color zoneColor = new Color(120, 190, 255);

        private readonly TypewriterCaches caches = new TypewriterCaches();
        private string rawTriggerSound, resolvedTriggerSound;

        public Vector2 DrawSize => new Vector2(item.Rect.Width, item.Rect.Height);

        public MapEntity PreviewEntity => item;

        partial void TriggerProjSpecific(Character character) {
            if (character != Character.Controlled) { return; }
            ScriptToast.Show(BuildSpec());
            PlayTriggerSound(character);
        }

        public ToastSpec BuildSpec() => TypewriterSpec.Build(this, item.Prefab.ContentPackage, caches);

        private void PlayTriggerSound(Character character) {
            if (SoundVolume <= 0.0f) { return; }
            string path = TypewriterText.ResolvePath(item, SoundFile, ref rawTriggerSound, ref resolvedTriggerSound);
            if (path == null) { return; }
            Sound sound = PrimaryPackage.Core.SoundStore.Absolute(path);
            if (sound != null) {
                SoundPlayer.PlaySound(sound, character.WorldPosition, volume: SoundVolume, range: 800.0f, hullGuess: character.CurrentHull);
            }
        }

        public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null) {
            if (!editing || Screen.Selected != GameMain.SubEditorScreen) { return; }
            if (!PrimaryPackage.Aes.AesAuxSystems.Visible(PrimaryPackage.Aes.AesAuxSystems.Kind.Script)) { return; }
            ZoneEditorOverlay.Draw(spriteBatch, item, zoneColor);
            if (item.IsSelected && MapEntity.SelectedList.Count == 1) {
                ScriptToast.SetPreview(this);
            }
        }
    }
}
