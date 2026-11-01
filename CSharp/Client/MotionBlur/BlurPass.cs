using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.MotionBlur;

internal static class BlurPass {
    internal const int Taps = 31;

    private const string ShaderPath = "Content/Effects/Shaders/ppmotionblur.mgfxo";
    private const float TapStep = 1.0f / (Taps - 1);

    private static Effect blur;
    private static EffectParameter sceneParameter;
    private static EffectParameter stepParameter;
    private static RenderTarget2D scratch;
    private static int builtWidth;
    private static int builtHeight;
    private static bool unavailable;

    internal static void Run(GraphicsDevice graphics, RenderTarget2D scene, float x, float y) {
        if (unavailable || !Ensure(graphics, scene)) { return; }
        Vector2 step = new Vector2(x * TapStep / scene.Width, y * TapStep / scene.Height);
        try {
            graphics.SetRenderTarget(scratch);
            graphics.BlendState = BlendState.Opaque;
            graphics.DepthStencilState = DepthStencilState.None;
            graphics.RasterizerState = RasterizerState.CullNone;
            graphics.SamplerStates[0] = SamplerState.LinearClamp;
            sceneParameter.SetValue(scene);
            stepParameter.SetValue(step);
            blur.CurrentTechnique.Passes[0].Apply();
            GraphicsQuad.Render();
            graphics.Textures[0] = null;
            graphics.SetRenderTarget(scene);
            GraphicsQuad.UseBasicEffect(scratch);
            GraphicsQuad.Render();
            graphics.Textures[0] = null;
        } catch (Exception error) {
            unavailable = true;
            Drop();
            try { graphics.SetRenderTarget(scene); } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("BlurPass.1", noted); }
            PrimaryPackage.Core.ModLog.Once($"motion blur stops after {error.Message}");
        }
    }

    private static bool Ensure(GraphicsDevice graphics, RenderTarget2D scene) {
        if (blur == null) {
            blur = PrimaryPackage.Core.ShaderStore.File(graphics, ShaderPath);
            if (blur == null) { unavailable = true; return false; }
            EffectTechnique technique = blur.Techniques["DirectionalBlur"];
            sceneParameter = blur.Parameters["xScene"];
            stepParameter = blur.Parameters["xStep"];
            if (technique == null || sceneParameter == null || stepParameter == null) {
                unavailable = true;
                return false;
            }
            blur.CurrentTechnique = technique;
        }
        if (scratch != null && !scratch.IsDisposed && builtWidth == scene.Width && builtHeight == scene.Height) {
            return true;
        }
        Drop();
        try {
            scratch = new RenderTarget2D(graphics, scene.Width, scene.Height, false, scene.Format,
                DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
        } catch (Exception) {
            unavailable = true;
            return false;
        }
        builtWidth = scene.Width;
        builtHeight = scene.Height;
        return true;
    }

    private static void Drop() {
        scratch?.Dispose();
        scratch = null;
        builtWidth = 0;
        builtHeight = 0;
    }

    internal static void Release() {
        Drop();
        blur = null;
        sceneParameter = null;
        stepParameter = null;
        unavailable = false;
    }
}
