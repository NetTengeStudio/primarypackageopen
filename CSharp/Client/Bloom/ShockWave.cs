using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Bloom {
    internal sealed class Wave {
        public Vector2 Position;
        public Vector2 Current => Field?.Origin ?? Position;
        public float MaxRadius;
        public float Duration;
        public float Delay;
        public float Age;
        public float Power;
        public float Amplitude;
        public Color Tint;
        public bool Underwater;
        public BlastField Field;
        public bool Shaken;
        public float Shake;
    }

    internal static class ShockWave {
        private const int Segments = BlastField.Rays;
        private const int Rings = 7;
        private const int RimRings = 3;
        private const int MaxWaves = 12;

        private static readonly float[] ringOffset = { -1f, -0.62f, -0.26f, 0f, 0.32f, 0.66f, 1f };
        private static readonly float[] ringShift = { 0f, 0.55f, 1f, 0.15f, -1f, -0.5f, 0f };
        private static readonly float[] ringAlpha = { 0f, 0.55f, 1f, 0.8f, 1f, 0.5f, 0f };
        private static readonly float[] rimOffset = { -0.4f, -0.05f, 0.3f };
        private static readonly float[] rimAlpha = { 0f, 1f, 0f };

        private static readonly List<Wave> waves = new List<Wave>();
        private static readonly VertexPositionColorTexture[] verts =
            new VertexPositionColorTexture[Rings * (Segments + 1)];
        private static readonly VertexPositionColor[] rimVerts =
            new VertexPositionColor[RimRings * (Segments + 1)];
        private static readonly short[] indices = BuildIndices(Rings);
        private static readonly short[] rimIndices = BuildIndices(RimRings);

        private static BasicEffect effect;
        private static BasicEffect rimEffect;
        private static bool disabled;

        private static short[] BuildIndices(int rings) {
            var result = new short[(rings - 1) * Segments * 6];
            int n = 0;
            for (int r = 0; r < rings - 1; r++) {
                int a = r * (Segments + 1), b = (r + 1) * (Segments + 1);
                for (int i = 0; i < Segments; i++) {
                    result[n++] = (short)(a + i);
                    result[n++] = (short)(b + i);
                    result[n++] = (short)(a + i + 1);
                    result[n++] = (short)(a + i + 1);
                    result[n++] = (short)(b + i);
                    result[n++] = (short)(b + i + 1);
                }
            }
            return result;
        }

        private const float RippleScale = 1.45f;

        internal static float MaxRadiusFor(float attackRange)
            => MathHelper.Clamp(attackRange * 4f, 320f, 7000f);

        internal const float WaterReach = 2.2f;
        private const float RippleThreshold = 400f;

        internal static float TraceRadiusFor(float attackRange, bool underwater)
            => MaxRadiusFor(attackRange) * RippleScale * (underwater ? WaterReach : 1f);

        internal static void Spawn(Vector2 worldPosition, BlastField field, float attackRange,
            Color tint, bool underwater) {
            if (disabled || ExplosionFlash.Shock <= 0.001f) { return; }
            float maxRadius = MaxRadiusFor(attackRange) * (underwater ? WaterReach : 1f);
            float power = MathHelper.Clamp(attackRange / 700f, 0.45f, 1.6f);
            float duration = MathHelper.Clamp(0.46f + attackRange / 1500f, 0.46f, 1.5f) * (underwater ? 1.7f : 1f);
            Add(worldPosition, field, maxRadius, duration, 0f, power, 1f, tint, underwater);
            if (attackRange < RippleThreshold) { return; }
            Add(worldPosition, field, maxRadius * RippleScale, duration * 1.9f, duration * 0.28f, power, 0.5f,
                tint, underwater);
        }

        private static void Add(Vector2 position, BlastField field, float maxRadius, float duration, float delay,
            float power, float amplitude, Color tint, bool underwater) {
            if (waves.Count >= MaxWaves) { waves.RemoveAt(0); }
            field?.Keep(delay + duration + 0.2f);
            waves.Add(new Wave {
                Position = position, Field = field,
                MaxRadius = maxRadius, Duration = duration, Delay = delay,
                Power = power, Amplitude = amplitude, Tint = tint,
                Underwater = underwater, Shake = amplitude
            });
        }

        internal static void Update(float deltaTime) {
            Camera cam = GameMain.GameScreen.Cam;
            for (int i = waves.Count - 1; i >= 0; i--) {
                Wave wave = waves[i];
                wave.Age += deltaTime;
                if (wave.Age >= wave.Delay + wave.Duration) { waves.RemoveAt(i); continue; }
                if (!wave.Shaken && cam != null && wave.Age >= wave.Delay) { Arrival(wave, cam); }
            }
        }

        private static void Arrival(Wave wave, Camera cam) {
            Vector2 listener = Character.Controlled?.WorldPosition ?? cam.Position;
            Vector2 offset = listener - wave.Current;
            float distance = offset.Length();
            float progress = (wave.Age - wave.Delay) / wave.Duration;
            float radius = wave.MaxRadius * (1f - (1f - progress) * (1f - progress));
            if (radius < distance) { return; }
            wave.Shaken = true;
            if (distance > wave.MaxRadius) { return; }
            float attenuation = 1f - MathHelper.Clamp(distance / wave.MaxRadius, 0f, 1f);
            attenuation *= wave.Underwater ? (float)Math.Sqrt(attenuation) : attenuation;
            float blocked = distance > 4f
                ? MathHelper.Clamp((wave.Field?.ReachTowards(listener) ?? distance) / distance, 0.3f, 1f)
                : 1f;
            float shake = wave.Shake * wave.Power * attenuation * blocked * 14f
                * MathHelper.Clamp(ExplosionFlash.Shock, 0f, 3f);
            if (shake > cam.Shake) { cam.Shake = shake; }
        }

        internal static void Draw(GraphicsDevice graphics, Texture2D scene) {
            if (disabled || waves.Count == 0 || scene == null || scene.IsDisposed) { return; }
            float shockScale = MathHelper.Clamp(ExplosionFlash.Shock, 0f, 3f);
            if (shockScale <= 0.001f) { return; }
            Camera cam = GameMain.GameScreen.Cam;
            if (cam == null) { return; }
            try {
                float w = GameMain.GraphicsWidth, h = GameMain.GraphicsHeight;
                effect ??= new BasicEffect(graphics) {
                    TextureEnabled = true, VertexColorEnabled = true,
                    World = Matrix.Identity, View = Matrix.Identity, Projection = Matrix.Identity
                };
                rimEffect ??= new BasicEffect(graphics) {
                    TextureEnabled = false, VertexColorEnabled = true,
                    World = Matrix.Identity, View = Matrix.Identity, Projection = Matrix.Identity
                };
                bool stateSet = false;
                foreach (Wave wave in waves) {
                    if (wave.Age < wave.Delay) { continue; }
                    float progress = (wave.Age - wave.Delay) / wave.Duration;
                    if (progress >= 1f) { continue; }
                    float ease = 1f - (1f - progress) * (1f - progress);
                    float worldRadius = wave.MaxRadius * ease;
                    float band = Math.Max(6f / Math.Max(cam.Zoom, 0.01f),
                        worldRadius * (0.12f + 0.07f * wave.Power));
                    float fade = wave.Underwater
                        ? (1f - progress) * (1f - progress * 0.55f)
                        : (1f - progress) * (1f - progress);
                    float strength = wave.Amplitude * MathHelper.Clamp(wave.Power, 0.45f, 1.6f) * shockScale;
                    float alpha = fade * 0.9f * strength;
                    float shift = (7f + 17f * wave.Power) * fade * wave.Amplitude * shockScale;
                    if (alpha <= 0.004f || worldRadius * cam.Zoom < 4f) { continue; }
                    Vector2 center = cam.WorldToScreen(wave.Current);
                    float reach = (worldRadius + band) * cam.Zoom + shift;
                    if (center.X + reach < 0f || center.X - reach > w
                        || center.Y + reach < 0f || center.Y - reach > h) { continue; }
                    for (int r = 0; r < Rings; r++) {
                        int baseIndex = r * (Segments + 1);
                        float offset = ringOffset[r] * band;
                        for (int i = 0; i <= Segments; i++) {
                            Vector2 dir = BlastField.Dir(i);
                            float limit = wave.Field?.Reach(i) ?? wave.MaxRadius;
                            float wanted = worldRadius + offset;
                            float clipped = wanted > limit ? 0.65f : 1f;
                            Vector2 screen = center + dir * (Math.Max(0f, Math.Min(wanted, limit)) * cam.Zoom);
                            Vector2 sample = screen + dir * (ringShift[r] * shift);
                            verts[baseIndex + i] = new VertexPositionColorTexture(
                                Clip(screen, w, h),
                                new Color(1f, 1f, 1f, ringAlpha[r] * alpha * clipped),
                                new Vector2(MathHelper.Clamp(sample.X / w, 0f, 1f),
                                    MathHelper.Clamp(sample.Y / h, 0f, 1f)));
                        }
                    }
                    float rim = fade * (float)Math.Sqrt(fade) * 0.3f * strength;
                    for (int r = 0; r < RimRings; r++) {
                        int baseIndex = r * (Segments + 1);
                        float offset = rimOffset[r] * band;
                        for (int i = 0; i <= Segments; i++) {
                            Vector2 dir = BlastField.Dir(i);
                            float limit = wave.Field?.Reach(i) ?? wave.MaxRadius;
                            float wanted = worldRadius + offset;
                            float clipped = wanted > limit ? 0.5f : 1f;
                            Vector2 screen = center + dir * (Math.Max(0f, Math.Min(wanted, limit)) * cam.Zoom);
                            float a = MathHelper.Clamp(rimAlpha[r] * rim * clipped, 0f, 1f);
                            rimVerts[baseIndex + i] = new VertexPositionColor(Clip(screen, w, h),
                                new Color(wave.Tint.R / 255f * a, wave.Tint.G / 255f * a,
                                    wave.Tint.B / 255f * a, a));
                        }
                    }
                    if (!stateSet) {
                        graphics.DepthStencilState = DepthStencilState.None;
                        graphics.RasterizerState = RasterizerState.CullNone;
                        graphics.SamplerStates[0] = SamplerState.LinearClamp;
                        effect.Texture = scene;
                        stateSet = true;
                    }
                    graphics.BlendState = BlendState.NonPremultiplied;
                    effect.CurrentTechnique.Passes[0].Apply();
                    graphics.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length,
                        indices, 0, indices.Length / 3);
                    if (rim <= 0.004f) { continue; }
                    graphics.BlendState = BlendState.Additive;
                    rimEffect.CurrentTechnique.Passes[0].Apply();
                    graphics.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, rimVerts, 0, rimVerts.Length,
                        rimIndices, 0, rimIndices.Length / 3);
                }
            } catch (Exception) {
                disabled = true;
                waves.Clear();
                DebugConsole.NewMessage("pp_bloom - ударная волна отключена", Color.Orange);
            }
        }

        private static Vector3 Clip(Vector2 screen, float w, float h)
            => new Vector3(screen.X / w * 2f - 1f, 1f - screen.Y / h * 2f, 0f);

        internal static void Clear() => waves.Clear();
    }
}
