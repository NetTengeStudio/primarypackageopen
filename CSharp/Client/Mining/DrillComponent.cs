using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Barotrauma.Sounds;
using FarseerPhysics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Mining {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    public partial class DrillComponent : IDrawableComponent {
        private const string AtlasKey = "drill/atlas";
        private static Texture2D atlas;
        private static Rectangle frameRect;
        private static bool bakeFailed;

        private string loopSoundPath;
        private float loopVolume = 4.0f;
        private float loopRange = 2500.0f;
        private SoundChannel loopChannel;

        private readonly List<string> breakSoundPaths = new List<string>();
        private int lastBreakSoundIndex = -1;

        private string overheatSoundPath;
        private float overheatVolume = 1.0f;
        private float overheatRange = 2500.0f;

        private Sprite coneSprite;
        private float animPhase, visIntensity, hitTimer, grip;
        private float rattleTimer, hammerPhase, wobble;
        private Vector2 drawOffset;
        private Vector2 rattle;
        private float sparkAcc, shardAcc, dustAcc, blackSmokeAcc, breakSoundTimer, steamAcc;

        public Vector2 DrawSize => coneSprite == null ? Vector2.Zero : coneSprite.size * item.Scale * 1.2f;

        partial void InitProjSpecific(ContentXElement element) {
            foreach (var subElement in element.Elements()) {
                switch (subElement.Name.ToString().ToLowerInvariant()) {
                    case "drillsprite":
                        coneSprite = new Sprite(subElement);
                        break;
                    case "loopsound":
                        loopSoundPath = subElement.GetAttributeContentPath("file")?.Value;
                        loopVolume = subElement.GetAttributeFloat("volume", loopVolume);
                        loopRange = subElement.GetAttributeFloat("range", loopRange);
                        break;
                    case "breaksound":
                        string path = subElement.GetAttributeContentPath("file")?.Value;
                        if (!string.IsNullOrEmpty(path)) { breakSoundPaths.Add(path); }
                        break;
                    case "overheatsound":
                        overheatSoundPath = subElement.GetAttributeContentPath("file")?.Value;
                        overheatVolume = subElement.GetAttributeFloat("volume", overheatVolume);
                        overheatRange = subElement.GetAttributeFloat("range", overheatRange);
                        break;
                }
            }
            breakSoundTimer = Rand.Range(2.0f, 4.5f);
            PrimaryPackage.Core.SoundStore.Absolute(loopSoundPath);
            PrimaryPackage.Core.SoundStore.Absolute(overheatSoundPath);
            foreach (string p in breakSoundPaths) { PrimaryPackage.Core.SoundStore.Absolute(p); }
        }

        partial void UpdateProjSpecific(float deltaTime) {
            UpdateLoopSound();
            UpdateSteam(deltaTime);
            if (!IsRunning && Intensity <= 0.0f) {
                drawOffset = rattle = Vector2.Zero;
                visIntensity = hitTimer = grip = wobble = 0.0f;
                return;
            }
            visIntensity = Intensity * Intensity * (3.0f - 2.0f * Intensity);
            animPhase = (animPhase + deltaTime * RotationSpeed * MathHelper.Lerp(0.1f, 1.0f, visIntensity)) % 1.0f;

            hitTimer = Math.Max(0.0f, hitTimer - deltaTime);
            float gripTarget = hitTimer > 0.0f ? 1.0f : 0.0f;
            grip += (gripTarget - grip) * Math.Min(1.0f, deltaTime * 8.0f);

            rattleTimer += deltaTime;
            float rattleInterval = 1.0f / MathHelper.Lerp(25.0f, 55.0f, visIntensity);
            if (rattleTimer >= rattleInterval) {
                rattleTimer %= rattleInterval;
                float amp = VibrationAmount * (0.7f + 1.3f * grip) * visIntensity;
                rattle = new Vector2(Rand.Range(-1.0f, 1.0f), Rand.Range(-1.0f, 1.0f)) * amp;
                if (grip > 0.3f && Rand.Range(0.0f, 1.0f) < 0.1f) { rattle *= 2.5f; }
                wobble = (rattle.X - rattle.Y) * 0.004f * (0.5f + 0.5f * grip);
            }

            hammerPhase = (hammerPhase + deltaTime * MathHelper.Lerp(6.0f, 13.0f, visIntensity)) % 1.0f;
            float impact = (1.0f - hammerPhase) * (1.0f - hammerPhase) * (1.0f - hammerPhase);
            Vector2 axis = DrillAxis();
            Vector2 perp = new Vector2(-axis.Y, axis.X);
            drawOffset = axis * (impact * (1.2f + 3.0f * grip) * visIntensity - grip * 3.0f)
                + perp * rattle.Y + new Vector2(rattle.X, 0.0f) * 0.6f;
            ShakeNearbyScreen();
        }

        private void UpdateLoopSound() {
            if (Intensity <= 0.0f || string.IsNullOrEmpty(loopSoundPath)) {
                ReleaseLoopChannel();
                return;
            }
            Sound sound = PrimaryPackage.Core.SoundStore.Absolute(loopSoundPath);
            if (sound == null) { return; }
            if (loopChannel == null || !loopChannel.IsPlaying) {
                loopChannel = sound.Play(0.01f, loopRange, item.WorldPosition);
                if (loopChannel != null) {
                    loopChannel.Looping = true;
                    loopChannel.Near = loopRange * 0.4f;
                    loopChannel.Far = loopRange;
                    SetChannelMaxGain(loopChannel, Math.Max(1.0f, loopVolume));
                }
            }
            if (loopChannel != null) {
                loopChannel.Gain = MathHelper.Lerp(loopVolume * 0.25f, loopVolume, Intensity);
                loopChannel.FrequencyMultiplier = MathHelper.Lerp(0.7f, 1.0f, visIntensity);
                loopChannel.Position = new Vector3(item.WorldPosition, 0.0f);
            }
        }

        private void ReleaseLoopChannel() {
            if (loopChannel == null) { return; }
            SetChannelMaxGain(loopChannel, 1.0f);
            loopChannel.FadeOutAndDispose();
            loopChannel = null;
        }

        private static void SetChannelMaxGain(SoundChannel channel, float maxGain) {
            if (channel == null || channel.ALSourceIndex < 0) { return; }
            uint source = channel.Sound.Owner.GetSourceFromIndex(channel.Sound.SourcePoolIndex, channel.ALSourceIndex);
            OpenAL.Al.Sourcef(source, OpenAL.Al.MaxGain, maxGain);
            OpenAL.Al.GetError();
        }

        private void UpdateSteam(float deltaTime) {
            if (GameMain.ParticleManager == null) { return; }
            float rate = Overheated ? 12.0f : Heat > 0.8f ? (Heat - 0.8f) * 20.0f : 0.0f;
            if (rate <= 0.0f) {
                steamAcc = 0.0f;
                return;
            }
            steamAcc += rate * deltaTime;
            while (steamAcc >= 1.0f) {
                steamAcc -= 1.0f;
                Vector2 tip = TipWorldPosition() + Rand.Vector(Rand.Range(0.0f, 20.0f));
                string prefab = IsTipInWater() ? "bubbles" : "steam";
                Vector2 vel = new Vector2(Rand.Range(-25.0f, 25.0f), Rand.Range(40.0f, 110.0f));
                GameMain.ParticleManager.CreateParticle(prefab, tip, vel, 0.0f, Hull.FindHull(tip, item.CurrentHull));
            }
        }

        private void ShakeNearbyScreen() {
            Character player = Character.Controlled;
            if (player == null || GameMain.GameScreen?.Cam == null) { return; }
            float dist = Vector2.Distance(player.WorldPosition, item.WorldPosition);
            if (dist >= ShakeRange) { return; }
            float amount = ShakeStrength * Intensity * (1.0f - dist / ShakeRange);
            GameMain.GameScreen.Cam.Shake = Math.Max(GameMain.GameScreen.Cam.Shake, amount);
        }

        partial void DrillHitProjSpecific(bool hit, Vector2 hitWorldPos, Vector2 axis, float deltaTime) {
            if (!hit) { return; }
            hitTimer = 0.25f;
            if (GameMain.ParticleManager != null) {
                Spawn(ref sparkAcc, 28.0f, deltaTime, "spark", hitWorldPos, axis, 150.0f, 450.0f);
                Spawn(ref shardAcc, 22.0f, deltaTime, "pp_drillshard", hitWorldPos, axis, 60.0f, 300.0f);
                Spawn(ref dustAcc, 1.5f, deltaTime, "pp_drillsmoke", hitWorldPos, axis, 5.0f, 20.0f);
                Spawn(ref blackSmokeAcc, 0.25f, deltaTime, "pp_bigblacksmoke", hitWorldPos, axis, 2.0f, 8.0f);
            }
            breakSoundTimer -= deltaTime;
            if (breakSoundTimer <= 0.0f) {
                breakSoundTimer = Rand.Range(2.5f, 5.0f);
                PlayRandomBreakSound(hitWorldPos);
            }
        }

        partial void OnBreakProjSpecific() {
            Sound sound = PrimaryPackage.Core.SoundStore.Absolute(overheatSoundPath);
            if (sound == null) { return; }
            SoundPlayer.PlaySound(sound, item.WorldPosition, volume: overheatVolume, range: overheatRange, hullGuess: item.CurrentHull);
        }

        private void PlayRandomBreakSound(Vector2 pos) {
            if (breakSoundPaths.Count == 0) { return; }
            int index = breakSoundPaths.Count == 1 ? 0 : Rand.Int(breakSoundPaths.Count - 1);
            if (breakSoundPaths.Count > 1 && index >= lastBreakSoundIndex) { index++; }
            lastBreakSoundIndex = index;
            Sound sound = PrimaryPackage.Core.SoundStore.Absolute(breakSoundPaths[index]);
            if (sound != null) {
                SoundPlayer.PlaySound(sound, pos, volume: 0.6f, range: 1500.0f, hullGuess: item.CurrentHull);
            }
        }

        private void Spawn(ref float acc, float rate, float deltaTime, string prefab, Vector2 pos, Vector2 axis, float velMin, float velMax) {
            acc += rate * deltaTime;
            while (acc >= 1.0f) {
                acc -= 1.0f;
                float a = (float)Math.Atan2(-axis.Y, -axis.X) + Rand.Range(-1.1f, 1.1f);
                Vector2 vel = new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * Rand.Range(velMin, velMax);
                GameMain.ParticleManager.CreateParticle(prefab, pos + Rand.Vector(6.0f), vel, 0.0f, item.CurrentHull);
            }
        }

        private static float Bump(float t, float center, float width) {
            float d = Math.Abs(t - center);
            d = Math.Min(d, 1.0f - d);
            return d > width ? 0.0f : 0.5f * (1.0f + (float)Math.Cos(Math.PI * d / width));
        }

        private void EnsureAtlas() {
            if (atlas is { IsDisposed: false }) { return; }
            atlas = null;
            if (bakeFailed || coneSprite == null) { return; }
            try {
                Rectangle src = coneSprite.SourceRect;
                Texture2D full = TextureLoader.FromFile(coneSprite.FilePath.Value, compress: false);
                if (full == null) { bakeFailed = true; return; }
                var baseData = new Color[src.Width * src.Height];
                full.GetData(0, src, baseData, 0, baseData.Length);
                full.Dispose();
                int n = Math.Max(2, FrameCount);
                float p = Math.Max(8.0f, ThreadPeriod);
                var colMinY = new int[src.Width];
                var colMaxY = new int[src.Width];
                for (int x = 0; x < src.Width; x++) { colMinY[x] = int.MaxValue; colMaxY[x] = -1; }
                for (int y = 0; y < src.Height; y++) {
                    for (int x = ScrollStart; x < src.Width; x++) {
                        if (baseData[y * src.Width + x].A >= 16) {
                            if (y < colMinY[x]) { colMinY[x] = y; }
                            if (y > colMaxY[x]) { colMaxY[x] = y; }
                        }
                    }
                }
                var outData = new Color[baseData.Length * (n + 2)];
                for (int f = 0; f <= n; f++) {
                    float off = (f - 1) * p / n;
                    int fo = f * baseData.Length;
                    for (int y = 0; y < src.Height; y++) {
                        for (int x = 0; x < src.Width; x++) {
                            int i = y * src.Width + x;
                            Color c = baseData[i];
                            if (f > 0 && c.A >= 16 && x >= ScrollStart) {
                                float ph = (x + y * ThreadSlope - off) % p;
                                if (ph < 0.0f) { ph += p; }
                                ph /= p;
                                float m = 1.0f
                                    + 0.75f * Bump(ph, 0.10f, 0.06f)
                                    - 0.55f * Bump(ph, 0.24f, 0.15f)
                                    + 0.25f * Bump(ph, 0.55f, 0.05f)
                                    - 0.30f * Bump(ph, 0.66f, 0.10f);
                                if (colMaxY[x] > colMinY[x]) {
                                    float t = (y - colMinY[x]) / (float)(colMaxY[x] - colMinY[x]);
                                    m *= 0.72f + 0.28f * (float)Math.Sin(Math.PI * t);
                                }
                                c = new Color(
                                    (byte)Math.Min(255.0f, c.R * m),
                                    (byte)Math.Min(255.0f, c.G * m),
                                    (byte)Math.Min(255.0f, c.B * m),
                                    c.A);
                            }
                            outData[fo + i] = c;
                        }
                    }
                }
                int go = (n + 1) * baseData.Length;
                float span = Math.Max(1.0f, src.Width - 1 - ScrollStart);
                for (int y = 0; y < src.Height; y++) {
                    for (int x = 0; x < src.Width; x++) {
                        int i = y * src.Width + x;
                        Color c = baseData[i];
                        if (c.A < 16 || x <= ScrollStart) {
                            outData[go + i] = Color.Transparent;
                            continue;
                        }
                        float w = (x - ScrollStart) / span;
                        w *= w;
                        float lum = (c.R * 0.3f + c.G * 0.59f + c.B * 0.11f) / 255.0f;
                        outData[go + i] = new Color(
                            (byte)255,
                            (byte)Math.Min(255.0f, 70.0f + 150.0f * lum),
                            (byte)Math.Min(255.0f, 25.0f + 60.0f * lum),
                            (byte)(c.A * w));
                    }
                }
                atlas = new Texture2D(GameMain.GraphicsDeviceManager.GraphicsDevice, src.Width, src.Height * (n + 2));
                atlas.SetData(outData);
                PrimaryPackage.Core.TextureStore.Adopt(AtlasKey, atlas);
                frameRect = new Rectangle(0, 0, src.Width, src.Height);
            } catch (Exception e) {
                bakeFailed = true;
                DebugConsole.ThrowError("drill frames do not bake", e);
            }
        }

        private static void DrawZone(SpriteBatch spriteBatch, Vector2[] corners, Color color) {
            for (int i = 0; i < corners.Length; i++) {
                Vector2 a = corners[i]; a.Y = -a.Y;
                Vector2 b = corners[(i + 1) % corners.Length]; b.Y = -b.Y;
                GUI.DrawLine(spriteBatch, a, b, color, width: 2);
            }
        }

        public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null) {
            if (editing && MapEntity.SelectedList.Contains(item)) {
                DrawZone(spriteBatch, GetZoneCorners(WallZoneLength, WallZoneWidth), GUIStyle.Blue);
                DrawZone(spriteBatch, GetZoneCorners(CharacterZoneLength, CharacterZoneWidth), GUIStyle.Red);
            }
            if (coneSprite == null) { return; }
            EnsureAtlas();
            if (atlas == null) { return; }
            bool running = Intensity > 0.0f && !editing;
            int n = atlas.Height / frameRect.Height - 2;
            int f = running ? 1 + Math.Min((int)(animPhase * n), n - 1) : 0;
            Rectangle src = new Rectangle(0, f * frameRect.Height, frameRect.Width, frameRect.Height);
            Vector2 offset = running ? drawOffset : Vector2.Zero;
            float rotJitter = running ? wobble : 0.0f;
            Vector2 pos = new Vector2(item.DrawPosition.X + offset.X, -(item.DrawPosition.Y + offset.Y));
            float depth = MathHelper.Clamp((itemDepth < 0.0f ? item.GetDrawDepth() : itemDepth) - 0.0002f, 0.0f, 0.999f);
            SpriteEffects fx = coneSprite.effects ^ item.SpriteEffects;
            spriteBatch.Draw(atlas, pos, src, overrideColor ?? item.GetSpriteColor(),
                item.RotationRad + rotJitter, coneSprite.Origin, item.Scale, fx, depth);

            float glow = MathHelper.Clamp((Heat - 0.55f) / 0.45f, 0.0f, 1.0f);
            if (glow > 0.0f && !editing) {
                Rectangle glowSrc = new Rectangle(0, (n + 1) * frameRect.Height, frameRect.Width, frameRect.Height);
                spriteBatch.Draw(atlas, pos, glowSrc, new Color(1.0f, 1.0f, 1.0f, glow),
                    item.RotationRad + rotJitter, coneSprite.Origin, item.Scale, fx, Math.Max(0.0f, depth - 0.0001f));
            }
        }

        public void ClientEventRead(IReadMessage msg, float sendingTime) {
            State = msg.ReadBoolean();
            float syncedIntensity = msg.ReadSingle();
            float syncedHeat = msg.ReadSingle();
            Overheated = msg.ReadBoolean();
            if (Math.Abs(syncedIntensity - Intensity) > 0.05f) { Intensity = syncedIntensity; }
            if (Math.Abs(syncedHeat - Heat) > 0.05f) { Heat = syncedHeat; }
        }

        public override void RemoveComponentSpecific() {
            base.RemoveComponentSpecific();
            coneSprite?.Remove();
            coneSprite = null;
            ReleaseLoopChannel();
        }
    }
}
