using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Particles;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Zones {
    public partial class FogZoneComponent : IDrawableComponent {
        private struct Bank {
            public Vector2 Pos;
            public Vector2 Dir;
            public float SpeedMul;
            public float Angle;
            public float Spin;
            public float Phase;
        }

        private struct Puff {
            public int Bank;
            public Vector2 Offset;
            public float Scale;
            public float Rotation;
            public float AngularVelocity;
            public float AlphaMul;
            public float Phase;
        }

        private const float AvgLifetime = 17.0f;
        private const float AreaPerCloud = 22000.0f;
        private const float MaxClouds = 120.0f;
        private const float ViewMargin = 512.0f;
        private const float BaseSpriteHalf = 64.0f;
        private const int PuffsPerBank = 6;
        private static readonly Color zoneColor = new Color(160, 165, 170);
        private static readonly List<FogZoneComponent> instances = new List<FogZoneComponent>();

        private string particlePrefabName;
        private float emitAcc;
        private bool wasVisible;
        private float simTime;
        private ParticlePrefab cloudPrefab;
        private readonly List<Bank> banks = new List<Bank>(24);
        private readonly List<Puff> puffs = new List<Puff>(128);

        public Vector2 DrawSize => new Vector2(item.Rect.Width, item.Rect.Height);

        partial void InitProjSpecific(ContentXElement element) {
            particlePrefabName = element.GetAttributeString("particleprefab", "pp_fogcloud");
            lock (instances) { instances.Add(this); }
        }

        public override void RemoveComponentSpecific() {
            base.RemoveComponentSpecific();
            lock (instances) { instances.Remove(this); }
        }

        partial void UpdateProjSpecific(float deltaTime) {
            if (GameMain.ParticleManager == null || Screen.Selected != GameMain.GameScreen ||
                FogAlpha <= 0.0f || Density <= 0.0f) {
                wasVisible = false;
                emitAcc = 0.0f;
                banks.Clear();
                puffs.Clear();
                return;
            }
            if (PersistentClouds) {
                wasVisible = false;
                emitAcc = 0.0f;
                UpdateDrift(deltaTime);
                return;
            }
            banks.Clear();
            puffs.Clear();
            Camera cam = Screen.Selected.Cam;
            if (cam == null) { return; }
            Rectangle view = cam.WorldView;
            Rectangle rect = item.WorldRect;
            bool visible =
                rect.X < view.X + view.Width + ViewMargin && view.X - ViewMargin < rect.X + rect.Width &&
                rect.Y - rect.Height < view.Y + ViewMargin && view.Y - view.Height - ViewMargin < rect.Y;
            if (!visible) {
                wasVisible = false;
                emitAcc = 0.0f;
                return;
            }
            float target = Math.Clamp(rect.Width * (float)rect.Height * Density / AreaPerCloud, 1.0f, MaxClouds);
            if (!wasVisible) {
                wasVisible = true;
                int prefill = Math.Max(1, (int)Math.Ceiling(target));
                for (int i = 0; i < prefill; i++) { SpawnCloud(rect); }
            }
            emitAcc = Math.Min(emitAcc + target / AvgLifetime * deltaTime, 8.0f);
            while (emitAcc >= 1.0f) {
                emitAcc -= 1.0f;
                SpawnCloud(rect);
            }
        }

        private float WrapMargin(Rectangle rect) {
            float margin = BaseSpriteHalf * 2.2f * CloudScale + 64.0f;
            return Math.Max(Math.Min(margin, Math.Min(rect.Width, rect.Height) * 0.45f), 1.0f);
        }

        private void UpdateDrift(float deltaTime) {
            simTime += deltaTime;
            Rectangle rect = item.Rect;
            float margin = WrapMargin(rect);
            float spanX = rect.Width + margin * 2.0f;
            float spanY = rect.Height + margin * 2.0f;
            int puffTarget = (int)Math.Clamp(rect.Width * (float)rect.Height * Density / AreaPerCloud, 1.0f, MaxClouds);
            int bankTarget = Math.Max(1, (puffTarget + PuffsPerBank - 1) / PuffsPerBank);
            if (puffs.Count != puffTarget || banks.Count != bankTarget) {
                RebuildField(puffTarget, bankTarget, spanX, spanY);
            }
            for (int i = 0; i < banks.Count; i++) {
                Bank b = banks[i];
                b.Pos += b.Dir * (DriftSpeed * b.SpeedMul * deltaTime);
                b.Angle += b.Spin * deltaTime;
                if (b.Pos.X < 0.0f) { b.Pos.X += spanX; } else if (b.Pos.X > spanX) { b.Pos.X -= spanX; }
                if (b.Pos.Y < 0.0f) { b.Pos.Y += spanY; } else if (b.Pos.Y > spanY) { b.Pos.Y -= spanY; }
                banks[i] = b;
            }
            for (int i = 0; i < puffs.Count; i++) {
                Puff p = puffs[i];
                p.Rotation += p.AngularVelocity * deltaTime;
                puffs[i] = p;
            }
        }

        private void RebuildField(int puffTarget, int bankTarget, float spanX, float spanY) {
            banks.Clear();
            puffs.Clear();
            for (int i = 0; i < bankTarget; i++) {
                float angle = Rand.Range(0.0f, MathHelper.TwoPi);
                banks.Add(new Bank {
                    Pos = new Vector2(Rand.Range(0.0f, spanX), Rand.Range(0.0f, spanY)),
                    Dir = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle) * 0.45f),
                    SpeedMul = Rand.Range(0.5f, 1.3f),
                    Angle = Rand.Range(0.0f, MathHelper.TwoPi),
                    Spin = Rand.Range(0.008f, 0.03f) * (Rand.Range(0, 2) == 0 ? -1.0f : 1.0f),
                    Phase = Rand.Range(0.0f, MathHelper.TwoPi)
                });
            }
            float bankRadius = Math.Min(BaseSpriteHalf * 3.6f * CloudScale, Math.Min(spanX, spanY) * 0.35f);
            for (int i = 0; i < puffTarget; i++) {
                float angle = Rand.Range(0.0f, MathHelper.TwoPi);
                float radius = bankRadius * (float)Math.Sqrt(Rand.Range(0.0f, 1.0f));
                float scale = Rand.Range(0.9f, 2.6f);
                puffs.Add(new Puff {
                    Bank = i % banks.Count,
                    Offset = new Vector2((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius * 0.7f),
                    Scale = scale,
                    Rotation = Rand.Range(0.0f, MathHelper.TwoPi),
                    AngularVelocity = Rand.Range(-0.03f, 0.03f),
                    AlphaMul = Rand.Range(0.4f, 1.0f) / (float)Math.Sqrt(scale),
                    Phase = Rand.Range(0.0f, MathHelper.TwoPi)
                });
            }
        }

        private ParticlePrefab ResolvePrefab() {
            if (cloudPrefab == null) {
                cloudPrefab = ParticleManager.FindPrefab(particlePrefabName);
            }
            return cloudPrefab;
        }

        internal static void DrawPersistent(SpriteBatch spriteBatch, bool inWater, bool? inSub) {
            lock (instances) {
                for (int i = 0; i < instances.Count; i++) {
                    instances[i].DrawClouds(spriteBatch, inWater, inSub);
                }
            }
        }

        private void DrawClouds(SpriteBatch spriteBatch, bool inWater, bool? inSub) {
            if (!PersistentClouds || puffs.Count == 0 || FogAlpha <= 0.0f) { return; }
            ParticlePrefab prefab = ResolvePrefab();
            if (prefab == null || prefab.Sprites.Count == 0) { return; }
            Camera cam = GameMain.GameScreen.Cam;
            if (cam == null) { return; }
            Rectangle rect = item.Rect;
            float margin = WrapMargin(rect);
            float spanX = rect.Width + margin * 2.0f;
            float spanY = rect.Height + margin * 2.0f;
            Vector2 center = item.DrawPosition;
            float left = center.X - rect.Width * 0.5f - margin;
            float top = center.Y + rect.Height * 0.5f + margin;
            Rectangle view = cam.WorldView;
            float maxExt = BaseSpriteHalf * 7.5f * CloudScale;
            if (left > view.X + view.Width + maxExt || left + spanX < view.X - maxExt ||
                top < view.Y - view.Height - maxExt || top - spanY > view.Y + maxExt) { return; }

            Sprite sprite = prefab.Sprites[0];
            for (int i = 0; i < puffs.Count; i++) {
                Puff p = puffs[i];
                Bank b = banks[p.Bank];
                float cos = (float)Math.Cos(b.Angle);
                float sin = (float)Math.Sin(b.Angle);
                Vector2 local = new Vector2(
                    b.Pos.X + p.Offset.X * cos - p.Offset.Y * sin,
                    b.Pos.Y + p.Offset.X * sin + p.Offset.Y * cos);
                Vector2 world = new Vector2(left + local.X, top - local.Y);
                float ext = BaseSpriteHalf * 1.5f * p.Scale * CloudScale;
                if (world.X < view.X - ext || world.X > view.X + view.Width + ext ||
                    world.Y > view.Y + ext || world.Y < view.Y - view.Height - ext) { continue; }

                Hull hull = FindZoneHull(world);
                bool water = hull == null || (hull.Submarine != null && world.Y < hull.WorldSurface);
                bool match = inWater
                    ? water && (inSub == null || (hull != null) == inSub.Value)
                    : !water;
                if (!match) { continue; }

                float edgeDist = Math.Min(Math.Min(local.X, spanX - local.X), Math.Min(local.Y, spanY - local.Y));
                float edge = Math.Clamp(edgeDist / margin, 0.0f, 1.0f);
                edge = edge * edge * (3.0f - 2.0f * edge);
                float thickness = 0.65f + 0.35f * (float)Math.Sin(simTime * 0.06f + b.Phase);
                float breathe = 0.85f + 0.15f * (float)Math.Sin(simTime * 0.3f + p.Phase);
                float a = FogAlpha * edge * p.AlphaMul * thickness * breathe;
                if (a <= 0.003f) { continue; }
                Color col = new Color(FogColor.R / 255.0f * a, FogColor.G / 255.0f * a, FogColor.B / 255.0f * a, a);
                sprite.Draw(spriteBatch, new Vector2(world.X, -world.Y), col, sprite.Origin,
                    p.Rotation, new Vector2(p.Scale * CloudScale), SpriteEffects.None, sprite.Depth);
            }
        }

        private void SpawnCloud(Rectangle rect) {
            var pos = new Vector2(
                rect.X + Rand.Range(0.0f, rect.Width),
                rect.Y - Rand.Range(0.0f, rect.Height));
            var vel = new Vector2(
                Rand.Range(-DriftSpeed, DriftSpeed),
                Rand.Range(-DriftSpeed * 0.5f, DriftSpeed * 0.5f));
            Particle particle = GameMain.ParticleManager.CreateParticle(particlePrefabName, pos, vel, 0.0f, FindZoneHull(pos));
            if (particle == null) { return; }
            particle.ColorMultiplier = new Vector4(FogColor.R / 255.0f, FogColor.G / 255.0f, FogColor.B / 255.0f, FogAlpha);
            particle.Size *= CloudScale;
        }

        public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null) {
            if (!editing || Screen.Selected != GameMain.SubEditorScreen) { return; }
            if (!PrimaryPackage.Aes.AesAuxSystems.Visible(PrimaryPackage.Aes.AesAuxSystems.Kind.Fog)) { return; }
            ZoneEditorOverlay.Draw(spriteBatch, item, zoneColor);
        }
    }

    public sealed class FogCloudRenderPlugin : PrimaryPackage.Core.IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            try {
                harmony.Patch(AccessTools.Method(typeof(ParticleManager), nameof(ParticleManager.Draw)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(FogCloudRenderPlugin), nameof(DrawPostfix))));
            }
            catch (Exception e) {
                DebugConsole.ThrowError("fogzone не удалось установить патч ParticleManager.Draw", e);
            }
        }


        public void Release() {
        }

        public static void DrawPostfix(SpriteBatch spriteBatch, bool inWater, bool? inSub, ParticleBlendState blendState) {
            if (blendState != ParticleBlendState.AlphaBlend || Screen.Selected != GameMain.GameScreen) { return; }
            FogZoneComponent.DrawPersistent(spriteBatch, inWater, inSub);
        }
    }
}
