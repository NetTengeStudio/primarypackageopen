using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Particles;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Thoughts;
using PrimaryPackage.Core;

namespace PrimaryPackage.Cold {
    public static class ColdVisuals {
        private const float ShiverFrequency = 13.0f;
        private const float FrostOverscan = 1.07f;
        private const float BreathScanInterval = 0.4f;
        private const float BreathRange = 1600.0f;

        private struct Breather {
            public Character Character;
            public Hull Hull;
            public float Phase;
            public float Period;
            public float Intensity;
            public float Accumulator;
        }

        private static readonly List<Breather> breathers = new List<Breather>();
        private static readonly List<Breather> scratch = new List<Breather>();

        private const string FrostKey = "cold/frost";
        private static Texture2D frost;
        private static Point frostSize;
        private static Character tracked;
        private static float level;
        private static float roomLevel;
        private static float shiverPhase;
        private static float burstPhase;
        private static float breath;
        private static float blinkTimer;
        private static float blinkPhase = -1.0f;
        private static float blink;
        private static float driftA;
        private static float driftB;
        private static float scanTimer;
        private static float criticalZoom;
        private static float heartPhase;
        private static float heatLevel;
        private static float heatDrift;
        private static float frostLevel;
        private static float painPhase;

        public static bool Enabled => PrimaryPackage.Core.ModSettings.ColdEffects.Value;

        public static float Level => level;

        public static float RoomLevel => roomLevel;

        public static float Breath => breath;

        private static void PublishZoom() =>
            PrimaryPackage.Core.ViewState.Contribute(PrimaryPackage.Core.ViewChannel.Cold, 1.0f + criticalZoom);

        public static void Reset() {
            tracked = null;
            level = 0.0f;
            roomLevel = 0.0f;
            criticalZoom = 0.0f;
            PublishZoom();
            shiverPhase = 0.0f;
            burstPhase = 0.0f;
            breath = 0.0f;
            blinkTimer = 0.0f;
            blinkPhase = -1.0f;
            blink = 0.0f;
            scanTimer = 0.0f;
            heartPhase = 0.0f;
            heatLevel = 0.0f;
            heatDrift = 0.0f;
            frostLevel = 0.0f;
            painPhase = 0.0f;
            breathers.Clear();
            scratch.Clear();
        }

        private static float RoomTemperature(Character character) {
            Hull hull = character.CurrentHull;
            return hull != null ? ColdField.GetSmoothed(hull)
                : ColdZoneComponent.TryGetOpenTemperature(character, out float open) ? open
                : ColdConfig.NoHullTemperature;
        }

        private static float RoomChill(Character character) {
            float comfort = ColdConfig.Comfort;
            float span = comfort - ColdConfig.MinTemperature;
            if (span <= 0.0f) { return 0.0f; }
            float t = (comfort - RoomTemperature(character)) / span;
            return t <= 0.0f ? 0.0f : t >= 1.0f ? 1.0f : t;
        }

        public static void Draw(SpriteBatch spriteBatch) {
            ThoughtClock.Tick();
            float dt = ThoughtClock.Step;
            Character character = Character.Controlled;
            if (!Enabled || GameMain.GameSession == null || Screen.Selected != GameMain.GameScreen ||
                character == null || character.Removed) {
                if (tracked != null) { Reset(); }
                return;
            }
            if (!ReferenceEquals(character, tracked)) {
                tracked = character;
                level = ColdAffliction.Normalized(character);
                roomLevel = RoomChill(character);
                heatLevel = ColdHyperthermia.Normalized(character);
            }

            float target = ColdAffliction.Normalized(character);
            if (target > 1.0f) { target = 1.0f; } else if (target < 0.0f) { target = 0.0f; }
            level = PrimaryPackage.Core.Scalar.Approach(level, target,
                target > level ? ColdConfig.RiseRate : ColdConfig.FallRate, dt);

            float room = RoomChill(character);
            roomLevel = PrimaryPackage.Core.Scalar.Approach(roomLevel, room,
                room > roomLevel ? ColdConfig.RiseRate : ColdConfig.FallRate, dt);

            float frost = PrimaryPackage.Core.Scalar.Clamp01(ColdFrostbite.Strength(character) / ColdFrostbite.MaxStrength);
            frostLevel = PrimaryPackage.Core.Scalar.Approach(frostLevel, frost,
                frost > frostLevel ? ColdConfig.RiseRate : ColdConfig.FallRate, dt);

            float heat = PrimaryPackage.Core.Scalar.Clamp01(ColdHyperthermia.Normalized(character));
            heatLevel = PrimaryPackage.Core.Scalar.Approach(heatLevel, heat,
                heat > heatLevel ? ColdConfig.RiseRate : ColdConfig.FallRate, dt);
            heatDrift += dt * ColdConfig.HeatDrift;
            if (heatDrift > 1024.0f) { heatDrift -= 1024.0f; }

            float severity = level > roomLevel * 0.55f ? level : roomLevel * 0.55f;
            float threshold = ColdStages.CriticalThreshold;
            float criticalT = threshold < 1.0f && level > threshold ? (level - threshold) / (1.0f - threshold) : 0.0f;
            criticalZoom = PrimaryPackage.Core.Scalar.Approach(criticalZoom, ColdConfig.CriticalZoomAmount * criticalT, ColdConfig.CriticalZoomRate, dt);
            PublishZoom();
            heartPhase += dt * MathHelper.Lerp(0.6f, ColdConfig.HeartbeatRate, criticalT);
            if (heartPhase > 1024.0f) { heartPhase -= 1024.0f; }
            UpdateBreath(dt);
            UpdateBlink(dt);
            Shiver(dt);
            EmitBreath(dt);
            painPhase += dt * MathHelper.Lerp(0.35f, 1.6f, frostLevel);
            if (painPhase > 1024.0f) { painPhase -= 1024.0f; }
            if (severity <= 0.004f && blink <= 0.004f && heatLevel <= 0.004f && frostLevel <= 0.004f) { return; }
            Overlay(spriteBatch, severity, criticalT);
        }

        private static float Heartbeat() {
            float t = heartPhase - (float)Math.Floor(heartPhase);
            float lub = Math.Max(0.0f, 1.0f - Math.Abs(t - 0.08f) / 0.06f);
            float dub = Math.Max(0.0f, 1.0f - Math.Abs(t - 0.24f) / 0.05f) * 0.6f;
            float pulse = lub * lub + dub * dub;
            return pulse > 1.0f ? 1.0f : pulse;
        }

        private static void UpdateBreath(float dt) {
            breath += dt / BreathPeriod(level);
            if (breath > 1024.0f) { breath -= 1024.0f; }
            driftA += dt * ColdConfig.FrostDrift;
            driftB += dt * ColdConfig.FrostDrift * 0.63f;
            if (driftA > 1024.0f) { driftA -= 1024.0f; }
            if (driftB > 1024.0f) { driftB -= 1024.0f; }
        }

        private static float Distress => level > heatLevel ? level : heatLevel;

        private static void UpdateBlink(float dt) {
            float level = Distress;
            if (ColdConfig.MaxBlink <= 0.0f || level < ColdConfig.BlinkThreshold) {
                blinkPhase = -1.0f;
                blinkTimer = 0.0f;
                blink = PrimaryPackage.Core.Scalar.Approach(blink, 0.0f, 5.0f, dt);
                return;
            }
            float duration = ColdConfig.BlinkDuration;
            if (duration < 0.15f) { duration = 0.15f; }
            if (blinkPhase >= 0.0f) {
                blinkPhase += dt / duration;
                if (blinkPhase >= 1.0f) {
                    blinkPhase = -1.0f;
                    blink = 0.0f;
                } else {
                    blink = PrimaryPackage.Core.Scalar.Smooth(1.0f - Math.Abs(blinkPhase * 2.0f - 1.0f));
                }
                return;
            }
            blink = PrimaryPackage.Core.Scalar.Approach(blink, 0.0f, 5.0f, dt);
            blinkTimer -= dt;
            if (blinkTimer > 0.0f) { return; }
            float interval = ColdConfig.BlinkInterval;
            if (interval < 1.0f) { interval = 1.0f; }
            float span = 1.0f - ColdConfig.BlinkThreshold;
            float deep = span <= 0.0f ? 1.0f : PrimaryPackage.Core.Scalar.Clamp01((level - ColdConfig.BlinkThreshold) / span);
            blinkTimer = interval * MathHelper.Lerp(1.25f, 0.55f, deep) * Rand.Range(0.75f, 1.25f);
            blinkPhase = 0.0f;
        }

        private static void Shiver(float dt) {
            if (level <= ColdConfig.ShakeThreshold) { return; }
            Camera cam = GameMain.GameScreen?.Cam;
            if (cam == null) { return; }
            float span = 1.0f - ColdConfig.ShakeThreshold;
            float t = span <= 0.0f ? 1.0f : (level - ColdConfig.ShakeThreshold) / span;
            float eased = ColdConfig.ShiverResist <= 0.0f
                ? PrimaryPackage.Core.Scalar.InCubic(t)
                : ColdAffliction.ShiverResistance(level) / ColdConfig.ShiverResist;

            burstPhase += dt * MathHelper.Lerp(ColdConfig.ShiverBurstMin, ColdConfig.ShiverBurstMax, t);
            if (burstPhase > 1024.0f) { burstPhase -= 1024.0f; }
            float duty = ColdConfig.ShiverDuty;
            if (duty < 0.05f) { duty = 0.05f; } else if (duty > 1.0f) { duty = 1.0f; }
            float wave = 0.5f + 0.5f * PrimaryPackage.Core.Scalar.Sine(burstPhase);
            float envelope = PrimaryPackage.Core.Scalar.Smooth(PrimaryPackage.Core.Scalar.Clamp01((wave - (1.0f - duty)) / duty));

            shiverPhase += dt * ShiverFrequency;
            float jitter = 0.62f + 0.38f * Math.Abs(PrimaryPackage.Core.Scalar.Noise(shiverPhase));
            float amount = eased * ColdConfig.MaxShake * (0.18f + 0.82f * envelope) * jitter;
            if (amount > cam.Shake) { cam.Shake = amount; }
        }

        private static float BreathPeriod(float strength) {
            float period = strength < 0.5f
                ? MathHelper.Lerp(ColdConfig.BreathPeriodWarm, ColdConfig.BreathPeriodFast, strength * 2.0f)
                : MathHelper.Lerp(ColdConfig.BreathPeriodFast, ColdConfig.BreathPeriodCold, (strength - 0.5f) * 2.0f);
            return period < 0.6f ? 0.6f : period;
        }

        private static void EmitBreath(float dt) {
            if (GameMain.ParticleManager == null || string.IsNullOrEmpty(ColdConfig.BreathParticle)) { return; }
            Camera cam = GameMain.GameScreen?.Cam;
            if (cam == null) { return; }
            scanTimer -= dt;
            if (scanTimer <= 0.0f) {
                scanTimer = BreathScanInterval;
                Rescan(cam);
            }

            float exhale = ColdConfig.BreathExhaleFraction;
            if (exhale < 0.05f) { exhale = 0.05f; } else if (exhale > 0.9f) { exhale = 0.9f; }
            float start = 1.0f - exhale;
            for (int i = 0; i < breathers.Count; i++) {
                Breather breather = breathers[i];
                Character character = breather.Character;
                if (character == null || character.Removed || character.IsDead) { continue; }
                float previous = breather.Phase;
                breather.Phase += dt / breather.Period;
                if (breather.Phase >= 1.0f) {
                    breather.Phase -= 1.0f;
                    breather.Accumulator = 0.0f;
                }
                if (breather.Phase >= start) {
                    float u = (breather.Phase - start) / exhale;
                    float profile = Profile(u);
                    breather.Accumulator += ColdConfig.BreathRate * profile * breather.Intensity * dt;
                    while (breather.Accumulator >= 1.0f) {
                        breather.Accumulator -= 1.0f;
                        Parcel(character, breather.Hull, breather.Intensity, profile);
                    }
                }
                breathers[i] = breather;
            }
        }

        private static float Profile(float u) {
            if (u <= 0.0f || u >= 1.0f) { return 0.0f; }
            float rise = u < 0.18f ? PrimaryPackage.Core.Scalar.OutCubic(u / 0.18f) : 1.0f;
            float fall = u < 0.18f ? 1.0f : 1.0f - PrimaryPackage.Core.Scalar.InCubic((u - 0.18f) / 0.82f);
            return rise * fall;
        }

        private static void Rescan(Camera cam) {
            Rectangle view = cam.WorldView;
            float falloff = ColdConfig.BreathFalloff;
            if (falloff < 1.0f) { falloff = 1.0f; }
            List<Character> all = Character.CharacterList;
            scratch.Clear();
            int kept = 0;
            for (int i = 0; i < all.Count; i++) {
                Character character = all[i];
                if (character.Removed || character.IsDead || !character.Enabled || !character.IsHuman) { continue; }
                if (character.AnimController == null || character.AnimController.InWater) { continue; }
                Hull hull = character.CurrentHull;
                if (hull == null) { continue; }
                float chill = (ColdConfig.BreathTemperature - ColdField.GetSmoothed(hull)) / falloff;
                if (chill <= 0.0f) { continue; }
                if (chill > 1.0f) { chill = 1.0f; }
                Vector2 position = character.WorldPosition;
                if (position.X < view.X - BreathRange || position.X > view.Right + BreathRange ||
                    position.Y > view.Y + BreathRange || position.Y < view.Y - view.Height - BreathRange) {
                    continue;
                }
                float strength = ColdAffliction.Normalized(character);
                float intensity = chill * (0.55f + 0.45f * strength);
                if (character.IsUnconscious) { intensity *= 0.4f; }

                Breather breather = new Breather { Character = character, Phase = Rand.Range(0.0f, 1.0f) };
                for (int j = 0; j < breathers.Count; j++) {
                    if (!ReferenceEquals(breathers[j].Character, character)) { continue; }
                    breather = breathers[j];
                    break;
                }
                breather.Hull = hull;
                breather.Period = BreathPeriod(strength);
                breather.Intensity = intensity;
                scratch.Add(breather);
                kept++;
            }
            breathers.Clear();
            for (int i = 0; i < scratch.Count; i++) { breathers.Add(scratch[i]); }
            scratch.Clear();
        }

        private static void Parcel(Character character, Hull hull, float intensity, float profile) {
            Limb head = character.AnimController?.GetLimb(LimbType.Head) ?? character.AnimController?.MainLimb;
            if (head == null || head.Removed) { return; }
            float dir = character.AnimController.Dir;
            var mouth = new Vector2(head.WorldPosition.X + ColdConfig.BreathMouthOffset * dir,
                head.WorldPosition.Y - ColdConfig.BreathMouthDrop);

            float angle = MathHelper.ToRadians(ColdConfig.BreathTilt +
                Rand.Range(-ColdConfig.BreathSpread, ColdConfig.BreathSpread) * 0.5f);
            var axis = new Vector2(dir * (float)Math.Cos(angle), -(float)Math.Sin(angle));
            float speed = ColdConfig.BreathSpeed * (0.45f + 0.55f * profile) * Rand.Range(0.75f, 1.15f);
            Vector2 origin = mouth + axis * ColdConfig.BreathGap * Rand.Range(0.6f, 1.4f);

            Particle particle = GameMain.ParticleManager.CreateParticle(
                ColdConfig.BreathParticle, origin, axis * speed, 0.0f, hull);
            if (particle == null) { return; }
            particle.Size *= ColdConfig.BreathScale * Rand.Range(0.85f, 1.2f) * (0.75f + 0.45f * intensity);
            particle.ColorMultiplier = new Vector4(1.0f, 1.0f, 1.0f, 0.35f + 0.65f * intensity);
            particle.VelocityChangeMultiplier = Rand.Range(0.7f, 1.35f);
        }

        private static void Overlay(SpriteBatch spriteBatch, float severity, float criticalT) {
            Texture2D white = GUI.WhiteTexture;
            if (white == null) { return; }
            var full = new Vector2(GameMain.GraphicsWidth, GameMain.GraphicsHeight);
            float pulse = 1.0f + 0.07f * PrimaryPackage.Core.Scalar.Sine(breath);
            float eased = PrimaryPackage.Core.Scalar.Smooth(severity);

            float uniform = eased * ColdConfig.MaxTint * ColdConfig.TintUniform * pulse;
            if (uniform > 0.002f) {
                spriteBatch.Draw(white, Vector2.Zero, null, ColdConfig.TintColor * uniform,
                    0.0f, Vector2.Zero, full, SpriteEffects.None, 0.0f);
            }

            float heatEased = PrimaryPackage.Core.Scalar.Smooth(heatLevel);
            float heatUniform = heatEased * ColdConfig.MaxHeatTint * ColdConfig.TintUniform * pulse;
            if (heatUniform > 0.002f) {
                spriteBatch.Draw(white, Vector2.Zero, null, ColdConfig.HeatTintColor * heatUniform,
                    0.0f, Vector2.Zero, full, SpriteEffects.None, 0.0f);
            }

            Texture2D mask = Frost();
            if (mask != null) {
                float edgeTint = eased * ColdConfig.MaxTint * (1.0f - ColdConfig.TintUniform) * 2.4f * pulse;
                Layer(spriteBatch, mask, full, ColdConfig.TintColor, edgeTint, driftA, false);
                float heartbeat = criticalT > 0.0f ? 1.0f + Heartbeat() * ColdConfig.HeartbeatPulse * criticalT : 1.0f;
                float deep = PrimaryPackage.Core.Scalar.InCubic(severity) * ColdConfig.MaxVignette * pulse * heartbeat;
                Layer(spriteBatch, mask, full, ColdConfig.VignetteColor, deep, driftB, true);
                float heatDeep = PrimaryPackage.Core.Scalar.InCubic(heatLevel) * ColdConfig.MaxHeatVignette *
                                 (1.0f + Heartbeat() * ColdConfig.HeartbeatPulse * heatLevel);
                Layer(spriteBatch, mask, full, ColdConfig.HeatVignetteColor, heatDeep, heatDrift, true);

                float pain = frostLevel * ColdConfig.MaxPain * (0.55f + 0.45f * PrimaryPackage.Core.Scalar.Sine(painPhase));
                Layer(spriteBatch, mask, full, ColdConfig.PainColor, pain, painPhase * 0.21f, false);
            }

            float grey = PrimaryPackage.Core.Scalar.InCubic(PrimaryPackage.Core.Scalar.Clamp01(
                (Distress - ColdConfig.GreyStart) / Math.Max(0.001f, 1.0f - ColdConfig.GreyStart))) * ColdConfig.MaxGrey;
            if (grey > 0.002f) {
                spriteBatch.Draw(white, Vector2.Zero, null, ColdConfig.GreyColor * grey,
                    0.0f, Vector2.Zero, full, SpriteEffects.None, 0.0f);
            }

            if (blink > 0.004f) {
                float alpha = PrimaryPackage.Core.Scalar.InCubic(blink) * ColdConfig.MaxBlink;
                spriteBatch.Draw(white, Vector2.Zero, null, ColdConfig.BlinkColor * alpha,
                    0.0f, Vector2.Zero, full, SpriteEffects.None, 0.0f);
            }
        }

        private static void Layer(SpriteBatch spriteBatch, Texture2D mask, Vector2 full, Color color, float alpha,
                                  float drift, bool flip) {
            if (alpha <= 0.002f) { return; }
            if (alpha > 1.0f) { alpha = 1.0f; }
            float wobbleX = PrimaryPackage.Core.Scalar.Sine(drift) * full.X * 0.012f;
            float wobbleY = PrimaryPackage.Core.Scalar.Sine(drift * 0.77f + 0.31f) * full.Y * 0.012f;
            Vector2 size = full * FrostOverscan;
            var origin = new Vector2((size.X - full.X) * 0.5f - wobbleX, (size.Y - full.Y) * 0.5f - wobbleY);
            spriteBatch.Draw(mask, -origin, null, color * alpha, 0.0f, Vector2.Zero,
                new Vector2(size.X / frostSize.X, size.Y / frostSize.Y),
                flip ? SpriteEffects.FlipHorizontally | SpriteEffects.FlipVertically : SpriteEffects.None, 0.0f);
        }

        private static Texture2D Frost() {
            if (frost != null && !frost.IsDisposed) { return frost; }
            if (GameMain.Instance?.GraphicsDevice == null) { return null; }
            Texture2D source = TextureLoader.FromFile(ModAssets.Resolve("Content/Afflictions/Overlays/pp_cold_overlay.png"), compress: false);
            if (source == null) { return null; }
            frostSize = new Point(source.Width, source.Height);
            var pixels = new Color[source.Width * source.Height];
            source.GetData(pixels);
            float start = ColdConfig.VignetteStart;
            float span = 1.0f - start;
            if (span <= 0.001f) { span = 0.001f; }
            for (int i = 0; i < pixels.Length; i++) {
                float remapped = PrimaryPackage.Core.Scalar.Clamp01((pixels[i].A / 255.0f - start) / span);
                byte value = (byte)(remapped * 255.0f + 0.5f);
                pixels[i] = new Color(value, value, value, value);
            }
            frost = new Texture2D(GameMain.Instance.GraphicsDevice, source.Width, source.Height);
            frost.SetData(pixels);
            PrimaryPackage.Core.TextureStore.Adopt(FrostKey, frost);
            source.Dispose();
            return frost;
        }

        public static void Dispose() {
            frost = null;
            breathers.Clear();
            scratch.Clear();
        }
    }
}
