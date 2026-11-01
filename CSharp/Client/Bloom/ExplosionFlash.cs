using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Lights;
using Barotrauma.Particles;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Bloom {
    public sealed class ExplosionFlashPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            harmony.Patch(AccessTools.Method(typeof(Explosion), "ExplodeProjSpecific"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ExplosionFlashPlugin), nameof(Explode_Postfix))));
        }

        private static void Explode_Postfix(Explosion __instance, Vector2 worldPosition) {
            try { ExplosionFlash.Spawn(__instance, worldPosition); }
            catch (Exception) { ExplosionFlash.Disable(); }
        }


        public void Release() {
            ExplosionFlash.Clear();
        }
    }

    internal sealed class Flash {
        public Vector2 Position;
        public BlastField Field;
        public Vector2 Current => Field?.Origin ?? Position;
        public float Range;
        public float Duration;
        public float Age;
        public float Power;
        public float Level;
        public Color Color;
        public LightSource Light;
        public LightSource AfterLight;
        public float AfterDuration;
        public float AfterLevel;
        public float Glow;
        public float GlowRadius;
    }

    internal static class ExplosionFlash {
        private const int MaxFlashes = 24;
        private const float AttackTime = 0.035f;
        private const float MinRange = 120f;
        private const float BlastRange = 300f;
        private const float WaveRange = 160f;
        private const int RecentCount = 8;
        private const float MergeWindow = 0.25f;

        internal static float Strength => PrimaryPackage.Core.ModSettings.ExplosionFlash.Value;
        internal static float Debris => PrimaryPackage.Core.ModSettings.ExplosionDebris.Value;
        internal static float Shock => PrimaryPackage.Core.ModSettings.ExplosionShock.Value;

        private static readonly Color WarmFlash = new Color(255, 168, 74);
        private static readonly Color Cooled = new Color(255, 96, 26);
        private const string DebrisPrefab = "explosiondebris";
        private const string SparkPrefab = "spark";

        private static readonly List<Flash> flashes = new List<Flash>();
        private static readonly AccessTools.FieldRef<Explosion, Color> flashColorRef =
            AccessTools.FieldRefAccess<Explosion, Color>("flashColor");
        private static readonly AccessTools.FieldRef<Explosion, bool> flashRef =
            AccessTools.FieldRefAccess<Explosion, bool>("flash");
        private static double prevTime;
        private static readonly Vector2[] recentPos = new Vector2[RecentCount];
        private static readonly float[] recentRange = new float[RecentCount];
        private static readonly double[] recentTime = new double[RecentCount];
        private static int recentIndex;
        private static bool disabled;
        private static BasicEffect glowEffect;
        private static readonly VertexPositionColor[] glowVerts =
            new VertexPositionColor[3 + BlastField.Rays * 2];
        private static readonly short[] glowIndices = BuildGlowIndices();

        internal static void Disable() {
            disabled = true;
            Clear();
            DebugConsole.NewMessage("pp_bloom - вспышки взрывов отключены", Color.Orange);
        }

        internal static void Spawn(Explosion explosion, Vector2 worldPosition) {
            if (disabled || Strength <= 0.001f || explosion == null) { return; }
            if (GameMain.Client is { MidRoundSyncing: true }) { return; }
            if (!flashRef(explosion)) { return; }
            float attackRange = explosion.Attack.Range;
            if (!IsBlast(explosion, attackRange) || Merged(worldPosition, attackRange)) { return; }
            float range = MathHelper.Clamp(attackRange * 2.5f, 120f, 6000f);
            Hull hull = Hull.FindHull(worldPosition, useWorldCoordinates: true);
            bool underwater = hull == null || worldPosition.Y < hull.WorldSurface;
            float traceRadius = Math.Max(range * 1.7f, ShockWave.TraceRadiusFor(attackRange, underwater));
            if (!NearCamera(worldPosition, traceRadius)) { return; }
            if (flashes.Count >= MaxFlashes) { Kill(flashes[0]); flashes.RemoveAt(0); }
            Color tint = Tint(flashColorRef(explosion));
            BlastField field = BlastField.Create(worldPosition, hull?.Submarine, traceRadius, 2f);
            var flash = new Flash {
                Position = worldPosition,
                Field = field,
                Range = range,
                Power = MathHelper.Clamp(attackRange / 700f, 0.45f, 1.6f),
                Duration = MathHelper.Clamp(0.08f + attackRange / 2500f, 0.08f, 0.6f),
                AfterDuration = MathHelper.Clamp(attackRange / 900f, 0.18f, 1.8f),
                Color = tint,
                Light = new LightSource(worldPosition, range, tint, null),
                AfterLight = new LightSource(worldPosition, range * 0.6f, tint, null)
            };
            flash.Light.Color = new Color(tint.R, tint.G, tint.B, (byte)0);
            flash.AfterLight.Color = new Color(tint.R, tint.G, tint.B, (byte)0);
            SpawnDebris(worldPosition, hull, attackRange);
            BlastSmoke.Spawn(worldPosition, hull, attackRange, field, underwater);
            if (attackRange >= WaveRange) { ShockWave.Spawn(worldPosition, field, attackRange, tint, underwater); }
            Remember(worldPosition, attackRange);
            flashes.Add(flash);
        }

        private static bool IsBlast(Explosion explosion, float attackRange) {
            if (attackRange < MinRange) { return false; }
            if (attackRange >= BlastRange) { return true; }
            Attack attack = explosion.Attack;
            return !MathUtils.NearlyEqual(attack.GetStructureDamage(1f), 0f)
                || !MathUtils.NearlyEqual(attack.GetLevelWallDamage(1f), 0f);
        }

        private static bool Merged(Vector2 worldPosition, float attackRange) {
            double now = Timing.TotalTime;
            for (int i = 0; i < RecentCount; i++) {
                if (now - recentTime[i] > MergeWindow) { continue; }
                if (recentRange[i] < attackRange) { continue; }
                float merge = Math.Max(120f, recentRange[i] * 0.8f);
                if (Vector2.DistanceSquared(recentPos[i], worldPosition) < merge * merge) { return true; }
            }
            return false;
        }

        private static void Remember(Vector2 worldPosition, float attackRange) {
            recentPos[recentIndex] = worldPosition;
            recentRange[recentIndex] = attackRange;
            recentTime[recentIndex] = Timing.TotalTime;
            recentIndex = (recentIndex + 1) % RecentCount;
        }

        private static Color Tint(Color source) {
            Color c = source == Color.LightYellow ? WarmFlash : source;
            float max = Math.Max(c.R, Math.Max(c.G, c.B));
            if (max < 1f) { return WarmFlash; }
            float k = 255f / max;
            return new Color((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        }

        private static bool NearCamera(Vector2 worldPosition, float slack) {
            Camera cam = GameMain.GameScreen.Cam;
            if (cam == null) { return false; }
            Rectangle view = cam.WorldView;
            return worldPosition.X > view.X - slack && worldPosition.X < view.Right + slack
                && worldPosition.Y < view.Y + slack && worldPosition.Y > view.Y - view.Height - slack;
        }

        private static void SpawnDebris(Vector2 worldPosition, Hull hull, float attackRange) {
            if (Debris <= 0.001f || !NearCamera(worldPosition, attackRange)) { return; }
            int count = (int)MathHelper.Clamp(attackRange * 0.06f * Debris, 1f, 26f);
            float speed = MathHelper.Clamp(400f + attackRange * 1.5f, 500f, 2200f);
            for (int i = 0; i < count; i++) {
                GameMain.ParticleManager.CreateParticle(DebrisPrefab, worldPosition,
                    Rand.Vector(Rand.Range(speed * 0.4f, speed)), 0f, hull);
                GameMain.ParticleManager.CreateParticle(SparkPrefab, worldPosition,
                    Rand.Vector(Rand.Range(speed * 0.6f, speed * 1.3f)), 0f, hull);
            }
        }

        private static void ApplyShockDistortion() {
            if (Shock <= 0.001f || Character.Controlled is not Character controlled) { return; }
            Camera cam = GameMain.GameScreen.Cam;
            if (cam == null) { return; }
            float strongest = 0f;
            foreach (Flash flash in flashes) {
                float distance = Vector2.Distance(cam.Position, flash.Current);
                float falloff = 1f - MathHelper.Clamp(distance / (flash.Range * 2.5f), 0f, 1f);
                strongest = Math.Max(strongest, Math.Max(flash.Level, flash.AfterLevel * 0.35f) * flash.Power * falloff);
            }
            if (strongest <= 0.001f) { return; }
            controlled.DistortStrength = Math.Max(controlled.DistortStrength, strongest * 0.007f * Shock);
        }

        internal static void Update() {
            double now = Timing.TotalTime;
            float dt = (float)Math.Min(Math.Max(now - prevTime, 0.0), 0.2);
            prevTime = now;
            BlastField.UpdateAll(dt);
            ShockWave.Update(dt);
            if (disabled) { return; }
            for (int i = flashes.Count - 1; i >= 0; i--) {
                Flash flash = flashes[i];
                flash.Age += dt;
                flash.Level = Curve(flash);
                float afterProgress = flash.AfterDuration <= 0f ? 1f : flash.Age / flash.AfterDuration;
                flash.AfterLevel = afterProgress >= 1f ? 0f
                    : (float)Math.Pow(1f - afterProgress, 1.6f) * Math.Min(1f, flash.Age / 0.06f);
                if (flash.Level <= 0.001f && flash.AfterLevel <= 0.001f) {
                    Kill(flash);
                    flashes.RemoveAt(i);
                    continue;
                }
                float strength = MathHelper.Clamp(Strength, 0f, 1f);
                if (flash.Light != null) {
                    if (flash.Field != null) { flash.Light.Position = flash.Current; }
                    flash.Light.Color = new Color(flash.Color.R, flash.Color.G, flash.Color.B,
                        (byte)MathHelper.Clamp(flash.Level * 255f * strength, 0f, 255f));
                }
                if (flash.AfterLight != null) {
                    if (flash.Field != null) { flash.AfterLight.Position = flash.Current; }
                    Color cooled = Color.Lerp(flash.Color, Cooled, MathHelper.Clamp(afterProgress, 0f, 1f));
                    flash.AfterLight.Range = flash.Range * (0.55f + 1.05f * MathHelper.Clamp(afterProgress, 0f, 1f));
                    flash.AfterLight.Color = new Color(cooled.R, cooled.G, cooled.B,
                        (byte)MathHelper.Clamp(flash.AfterLevel * 150f * strength, 0f, 255f));
                }
                flash.Glow = Math.Max(flash.Level, flash.AfterLevel * 0.5f);
                flash.GlowRadius = flash.Range * (1f + 0.55f * MathHelper.Clamp(afterProgress, 0f, 1f));
            }
            ApplyShockDistortion();
        }

        internal static void Inject(GraphicsDevice graphics, RenderTarget2D target) {
            if (disabled || flashes.Count == 0 || Strength <= 0.001f) { return; }
            try { InjectQuads(graphics, target); }
            catch (Exception) { Disable(); }
        }

        private static void InjectQuads(GraphicsDevice graphics, RenderTarget2D target) {
            Camera cam = GameMain.GameScreen.Cam;
            if (cam == null) { return; }
            float w = GameMain.GraphicsWidth, h = GameMain.GraphicsHeight;
            glowEffect ??= new BasicEffect(graphics) {
                VertexColorEnabled = true, TextureEnabled = false,
                World = Matrix.Identity, View = Matrix.Identity, Projection = Matrix.Identity
            };
            bool bound = false;
            foreach (Flash flash in flashes) {
                float level = flash.Glow * MathHelper.Clamp(Strength, 0f, 2f) * 0.7f;
                if (level <= 0.004f) { continue; }
                Vector2 center = cam.WorldToScreen(flash.Current);
                float reach = flash.GlowRadius * cam.Zoom;
                if (reach < 3f || center.X + reach < 0f || center.X - reach > w
                    || center.Y + reach < 0f || center.Y - reach > h) { continue; }
                var core = new Color(flash.Color.R / 255f * level, flash.Color.G / 255f * level,
                    flash.Color.B / 255f * level, level);
                var mid = core * 0.45f;
                var edge = Color.Transparent;
                glowVerts[0] = Vertex(center, w, h, core);
                for (int i = 0; i <= BlastField.Rays; i++) {
                    Vector2 dir = BlastField.Dir(i);
                    float limit = flash.Field?.Reach(i) ?? flash.Range;
                    float radius = Math.Min(flash.GlowRadius, limit) * cam.Zoom;
                    glowVerts[1 + i] = Vertex(center + dir * (radius * 0.45f), w, h, mid);
                    glowVerts[2 + BlastField.Rays + i] = Vertex(center + dir * radius, w, h, edge);
                }
                if (!bound) {
                    graphics.SetRenderTarget(target);
                    graphics.BlendState = BlendState.Additive;
                    graphics.DepthStencilState = DepthStencilState.None;
                    graphics.RasterizerState = RasterizerState.CullNone;
                    bound = true;
                }
                glowEffect.CurrentTechnique.Passes[0].Apply();
                graphics.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, glowVerts, 0, glowVerts.Length,
                    glowIndices, 0, glowIndices.Length / 3);
            }
        }

        private static VertexPositionColor Vertex(Vector2 screen, float w, float h, Color color)
            => new VertexPositionColor(new Vector3(screen.X / w * 2f - 1f, 1f - screen.Y / h * 2f, 0f), color);

        private static short[] BuildGlowIndices() {
            int rays = BlastField.Rays;
            var result = new short[rays * 3 + rays * 6];
            int n = 0;
            for (int i = 0; i < rays; i++) {
                result[n++] = 0;
                result[n++] = (short)(1 + i);
                result[n++] = (short)(2 + i);
            }
            int outer = 2 + rays;
            for (int i = 0; i < rays; i++) {
                result[n++] = (short)(1 + i);
                result[n++] = (short)(outer + i);
                result[n++] = (short)(2 + i);
                result[n++] = (short)(2 + i);
                result[n++] = (short)(outer + i);
                result[n++] = (short)(outer + i + 1);
            }
            return result;
        }

        private static float Curve(Flash flash) {
            if (flash.Age < AttackTime) { return flash.Age / AttackTime; }
            float decay = (flash.Age - AttackTime) / flash.Duration;
            return decay >= 1f ? 0f : (float)Math.Pow(1f - decay, 3f);
        }

        private static void Kill(Flash flash) {
            flash.Light?.Remove();
            flash.Light = null;
            flash.AfterLight?.Remove();
            flash.AfterLight = null;
        }

        internal static void Clear() {
            foreach (Flash flash in flashes) { Kill(flash); }
            flashes.Clear();
            ShockWave.Clear();
            BlastField.Clear();
        }
    }
}
