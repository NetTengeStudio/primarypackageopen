using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Particles;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Zones {
    public partial class RadiationZoneComponent : IDrawableComponent {
        private static readonly Color zoneColor = new Color(110, 220, 120);

        private string particlePrefabName;
        private string geigerSoundPath;
        private SoundChannel geigerChannel;
        private float emitAcc;

        partial void InitProjSpecific(ContentXElement element) {
            particlePrefabName = element.GetAttributeString("particleprefab", "pp_radiationdust");
            foreach (var subElement in element.Elements()) {
                if (subElement.Name.ToString().Equals("geigersound", StringComparison.OrdinalIgnoreCase)) {
                    geigerSoundPath = subElement.GetAttributeContentPath("file")?.Value;
                }
            }
        }

        partial void UpdateProjSpecific(float deltaTime) {
            UpdateParticles(deltaTime);
            UpdateGeiger();
        }

        private void UpdateParticles(float deltaTime) {
            if (GameMain.ParticleManager == null || occupants.Count == 0 ||
                ParticlesPerSecond <= 0.0f || ParticleAlpha <= 0.0f) {
                emitAcc = 0.0f;
                return;
            }
            emitAcc = Math.Min(emitAcc + ParticlesPerSecond * deltaTime, 30.0f);
            if (emitAcc < 1.0f) { return; }
            Rectangle worldRect = item.WorldRect;
            Vector4 color = new Vector4(ParticleColor.R / 255.0f, ParticleColor.G / 255.0f, ParticleColor.B / 255.0f, ParticleAlpha);
            string prefabName = string.IsNullOrWhiteSpace(ParticlePrefabOverride) ? particlePrefabName : ParticlePrefabOverride;
            float scaleMin = Math.Min(ParticleScaleMin, ParticleScaleMax);
            float scaleMax = Math.Max(ParticleScaleMin, ParticleScaleMax);
            float riseMin = Math.Min(ParticleRiseMin, ParticleRiseMax);
            float riseMax = Math.Max(ParticleRiseMin, ParticleRiseMax);
            while (emitAcc >= 1.0f) {
                emitAcc -= 1.0f;
                Vector2 pos = new Vector2(
                    worldRect.X + Rand.Range(0.0f, worldRect.Width),
                    worldRect.Y - Rand.Range(0.0f, worldRect.Height));
                Vector2 vel = new Vector2(Rand.Range(-ParticleDrift, ParticleDrift), Rand.Range(riseMin, riseMax));
                Particle particle = GameMain.ParticleManager.CreateParticle(prefabName, pos, vel, 0.0f, FindZoneHull(pos));
                if (particle != null) {
                    particle.ColorMultiplier = color;
                    particle.Size *= Rand.Range(scaleMin, scaleMax);
                }
            }
        }

        private void UpdateGeiger() {
            Character local = Character.Controlled;
            bool audible = GeigerVolume > 0.0f && !string.IsNullOrEmpty(geigerSoundPath) &&
                local != null && !local.Removed && occupants.Contains(local);
            if (!audible) {
                ReleaseGeigerChannel();
                return;
            }
            Sound sound = PrimaryPackage.Core.SoundStore.Absolute(geigerSoundPath);
            if (sound == null) { return; }
            if (geigerChannel == null || !geigerChannel.IsPlaying) {
                geigerChannel = sound.Play(0.01f, 1000.0f, local.WorldPosition);
                if (geigerChannel != null) { geigerChannel.Looping = true; }
            }
            if (geigerChannel != null) {
                geigerChannel.Gain = GeigerVolume;
                geigerChannel.Position = new Vector3(local.WorldPosition, 0.0f);
            }
        }

        private void ReleaseGeigerChannel() {
            if (geigerChannel == null) { return; }
            geigerChannel.FadeOutAndDispose();
            geigerChannel = null;
        }

        public Vector2 DrawSize => new Vector2(item.Rect.Width, item.Rect.Height);

        public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null) {
            if (!editing || Screen.Selected != GameMain.SubEditorScreen) { return; }
            if (!PrimaryPackage.Aes.AesAuxSystems.Visible(PrimaryPackage.Aes.AesAuxSystems.Kind.Radiation)) { return; }
            ZoneEditorOverlay.Draw(spriteBatch, item, zoneColor);
        }

        public override void RemoveComponentSpecific() {
            base.RemoveComponentSpecific();
            ReleaseGeigerChannel();
        }
    }
}
