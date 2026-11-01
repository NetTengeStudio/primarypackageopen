using System;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.Bloom {
    internal static class BloomChain {
        internal const int MaxLevels = 6;

        private const int MinSize = 4;
        private const string ShaderPath = "Content/Effects/Shaders/ppbloom.mgfxo";

        internal static bool SameFrameSource;

        private static readonly RenderTarget2D[] chain = new RenderTarget2D[MaxLevels];
        private static readonly BlendState addBlend = new BlendState {
            ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.One
        };

        private static readonly AccessTools.FieldRef<GameScreen, float> fadeToBlack =
            AccessTools.FieldRefAccess<GameScreen, float>("fadeToBlackState");

        private static readonly AccessTools.FieldRef<SpriteBatch, bool> batchBegun = MakeBatchRef();

        private static Effect effect;
        private static EffectTechnique prefilterPass, downsamplePass, upsamplePass;
        private static EffectParameter sceneParameter, texelParameter, curveParameter;
        private static EffectParameter multiplierParameter, radiusParameter, spreadParameter;

        private static SurfaceFormat chainFormat = SurfaceFormat.HdrBlendable;
        private static int builtWidth, builtHeight, builtLevels;
        private static bool unavailable;
        private static bool chainReady;

        private static AccessTools.FieldRef<SpriteBatch, bool> MakeBatchRef() {
            try { return AccessTools.FieldRefAccess<SpriteBatch, bool>("_beginCalled"); }
            catch (Exception) { return null; }
        }

        private static bool BatchBusy(SpriteBatch spriteBatch) => batchBegun != null && batchBegun(spriteBatch);

        internal static RenderTarget2D Source(GameScreen screen) => PrimaryPackage.Core.SceneTarget.Scene(screen);

        private static int Levels =>
            Math.Clamp(PrimaryPackage.Core.ModSettings.BloomLevels.Value, 1, MaxLevels);

        private static float ChainGain {
            get {
                float s = MathHelper.Clamp(PrimaryPackage.Core.ModSettings.BloomSpread.Value, 0.05f, 0.95f);
                return (1f - (float)Math.Pow(s, builtLevels)) / (1f - s);
            }
        }

        internal static void BuildChain(GameScreen screen, GraphicsDevice graphics, SpriteBatch spriteBatch) {
            chainReady = false;
            if (!PrimaryPackage.Core.ModSettings.BloomEnabled.Value || unavailable
                || PrimaryPackage.Core.ModSettings.BloomIntensity.Value <= 0.001f) { return; }
            RenderTarget2D source = PrimaryPackage.Core.SceneTarget.Scene(screen);
            if (source == null || source.IsDisposed || BatchBusy(spriteBatch)) { return; }
            try {
                if (!EnsureResources(graphics)) { return; }
                Prefilter(graphics, source);
                ExplosionFlash.Inject(graphics, chain[0]);
                for (int i = 1; i < builtLevels; i++) { Downsample(graphics, i); }
                for (int i = builtLevels - 1; i > 0; i--) { Upsample(graphics, i); }
                graphics.Textures[0] = null;
                chainReady = true;
            } catch (Exception error) {
                ReleaseTargets();
                Abort(graphics, error);
            }
        }

        internal static void Composite(GameScreen screen, SpriteBatch spriteBatch) {
            if (!chainReady) { return; }
            chainReady = false;
            float fade = 1f - MathHelper.Clamp(fadeToBlack(screen), 0f, 1f);
            if (fade <= 0.001f || BatchBusy(spriteBatch)) { return; }
            try {
                spriteBatch.Begin(SpriteSortMode.Deferred, addBlend, SamplerState.LinearClamp,
                    DepthStencilState.None, RasterizerState.CullNone);
                spriteBatch.Draw(chain[0], new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight),
                    Color.White * (PrimaryPackage.Core.ModSettings.BloomIntensity.Value * fade / ChainGain));
                spriteBatch.End();
            } catch (Exception error) {
                try { spriteBatch.End(); } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("BloomChain.1", noted); }
                Abort(null, error);
            }
        }

        private static void Abort(GraphicsDevice graphics, Exception error) {
            unavailable = true;
            chainReady = false;
            try { graphics?.SetRenderTarget(null); } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("BloomChain.2", noted); }
            PrimaryPackage.Core.ModLog.Once($"bloom stops after {error.Message}");
        }

        private static bool EnsureResources(GraphicsDevice graphics) {
            if (effect == null && !LoadEffect(graphics)) { return false; }
            int w = GameMain.GraphicsWidth, h = GameMain.GraphicsHeight, levels = Levels;
            if (w < MinSize * 2 || h < MinSize * 2) { return false; }
            if (w == builtWidth && h == builtHeight && levels == builtLevels
                && chain[0] != null && !chain[0].IsDisposed) { return true; }
            ReleaseTargets();
            for (int i = 0; i < levels; i++) {
                int lw = Math.Max(MinSize, w >> (i + 1)), lh = Math.Max(MinSize, h >> (i + 1));
                chain[i] = CreateTarget(graphics, lw, lh);
                if (chain[i] == null) { ReleaseTargets(); unavailable = true; return false; }
            }
            builtWidth = w; builtHeight = h; builtLevels = levels;
            return true;
        }

        private static bool LoadEffect(GraphicsDevice graphics) {
            effect = PrimaryPackage.Core.ShaderStore.File(graphics, ShaderPath);
            if (effect == null) { unavailable = true; return false; }
            prefilterPass = effect.Techniques["BloomPrefilter"];
            downsamplePass = effect.Techniques["BloomDownsample"];
            upsamplePass = effect.Techniques["BloomUpsample"];
            sceneParameter = effect.Parameters["xScene"];
            texelParameter = effect.Parameters["xTexel"];
            curveParameter = effect.Parameters["xCurve"];
            multiplierParameter = effect.Parameters["xMultiplier"];
            radiusParameter = effect.Parameters["xRadius"];
            spreadParameter = effect.Parameters["xSpread"];
            if (prefilterPass == null || downsamplePass == null || upsamplePass == null
                || sceneParameter == null || texelParameter == null || curveParameter == null
                || multiplierParameter == null || radiusParameter == null || spreadParameter == null) {
                effect = null;
                unavailable = true;
                return false;
            }
            return true;
        }

        private static RenderTarget2D CreateTarget(GraphicsDevice graphics, int w, int h) {
            try {
                return new RenderTarget2D(graphics, w, h, false, chainFormat, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            } catch (Exception) {
                if (chainFormat == SurfaceFormat.HdrBlendable) {
                    chainFormat = SurfaceFormat.Color;
                    return CreateTarget(graphics, w, h);
                }
                return null;
            }
        }

        private static Vector4 Curve() {
            float threshold = MathHelper.Clamp(PrimaryPackage.Core.ModSettings.BloomThreshold.Value, 0f, 1f);
            float knee = Math.Max(threshold * MathHelper.Clamp(PrimaryPackage.Core.ModSettings.BloomKnee.Value, 0f, 1f), 0.0001f);
            return new Vector4(threshold - knee, knee * 2f, 0.25f / knee, threshold);
        }

        private static void Blit(GraphicsDevice graphics, EffectTechnique technique, Texture2D src,
            RenderTarget2D dst, int srcWidth, int srcHeight, BlendState blend) {
            graphics.SetRenderTarget(dst);
            if (blend == BlendState.Opaque) { graphics.Clear(Color.Transparent); }
            graphics.BlendState = blend;
            graphics.DepthStencilState = DepthStencilState.None;
            graphics.RasterizerState = RasterizerState.CullNone;
            graphics.SamplerStates[0] = SamplerState.LinearClamp;
            sceneParameter.SetValue(src);
            texelParameter.SetValue(new Vector2(1f / srcWidth, 1f / srcHeight));
            effect.CurrentTechnique = technique;
            technique.Passes[0].Apply();
            GraphicsQuad.Render();
            graphics.Textures[0] = null;
        }

        private static void Prefilter(GraphicsDevice graphics, RenderTarget2D source) {
            curveParameter.SetValue(Curve());
            multiplierParameter.SetValue(
                MathHelper.Clamp(PrimaryPackage.Core.ModSettings.BloomMultiplier.Value, 0.1f, 8f));
            Blit(graphics, prefilterPass, source, chain[0], source.Width, source.Height, BlendState.Opaque);
        }

        private static void Downsample(GraphicsDevice graphics, int level) {
            RenderTarget2D src = chain[level - 1];
            Blit(graphics, downsamplePass, src, chain[level], src.Width, src.Height, BlendState.Opaque);
        }

        private static void Upsample(GraphicsDevice graphics, int level) {
            RenderTarget2D src = chain[level];
            radiusParameter.SetValue(MathHelper.Clamp(PrimaryPackage.Core.ModSettings.BloomRadius.Value, 0.25f, 4f));
            spreadParameter.SetValue(MathHelper.Clamp(PrimaryPackage.Core.ModSettings.BloomSpread.Value, 0.05f, 0.95f));
            Blit(graphics, upsamplePass, src, chain[level - 1], src.Width, src.Height, addBlend);
        }

        private static void ReleaseTargets() {
            for (int i = 0; i < MaxLevels; i++) {
                chain[i]?.Dispose();
                chain[i] = null;
            }
            builtWidth = builtHeight = builtLevels = 0;
        }

        internal static void Release() {
            ReleaseTargets();
            effect = null;
            prefilterPass = downsamplePass = upsamplePass = null;
            sceneParameter = texelParameter = curveParameter = null;
            multiplierParameter = radiusParameter = spreadParameter = null;
            unavailable = false;
        }

        internal static void Reconsider() => unavailable = false;
    }
}
