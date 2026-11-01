using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Barotrauma;

namespace PrimaryPackage.TextMotion;
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif



internal sealed class TextPainter {
    private SpriteBatch batch;
    private bool gpu, joined;
    private Rectangle outer;

    internal void Begin(SpriteBatch spriteBatch, bool joinGui) {
        if (batch != null) { throw new InvalidOperationException("Nested text pass"); }
        bool ready = TypeGlyphRenderer.Ready(spriteBatch.GraphicsDevice);
        Rectangle clip = spriteBatch.GraphicsDevice.ScissorRectangle;
        bool guiEnded = false;
        try {
            if (ready) {
                if (joinGui) { spriteBatch.End(); guiEnded = true; }
                TypeGlyphRenderer.Begin(spriteBatch.GraphicsDevice);
            } else if (!joinGui) {
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp, DepthStencilState.None, GameMain.ScissorTestEnable);
            }
        } catch {
            try { if (ready) { TypeGlyphRenderer.End(); } }
            finally {
                if (guiEnded) { spriteBatch.Begin(SpriteSortMode.Deferred, samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable); }
            }
            throw;
        }
        batch = spriteBatch; joined = joinGui; outer = clip; gpu = ready;
    }

    internal void End() {
        if (batch == null) { return; }
        SpriteBatch active = batch;
        batch = null;
        try {
            if (gpu) { TypeGlyphRenderer.End(); }
            else if (!joined) { active.End(); }
        } finally {
            active.GraphicsDevice.ScissorRectangle = outer;
            if (gpu && joined) {
                active.Begin(SpriteSortMode.Deferred, samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable);
            }
        }
    }

    internal Rectangle Clip(in Box box) {
        Rectangle saved = batch.GraphicsDevice.ScissorRectangle;
        Rectangle next = Rectangle.Intersect(saved, new Rectangle((int)MathF.Floor(box.X), (int)MathF.Floor(box.Y),
            (int)MathF.Ceiling(box.Right) - (int)MathF.Floor(box.X), (int)MathF.Ceiling(box.Bottom) - (int)MathF.Floor(box.Y)));
        Scissor(next);
        return saved;
    }

    internal void Scissor(Rectangle rectangle) {
        if (gpu) { TypeGlyphRenderer.FlushBeforeClip(); }
        else { batch.End(); }
        batch.GraphicsDevice.ScissorRectangle = rectangle;
        if (!gpu) {
            batch.Begin(SpriteSortMode.Deferred, joined ? null : BlendState.NonPremultiplied,
                joined ? GUI.SamplerState : SamplerState.LinearClamp, DepthStencilState.None, GameMain.ScissorTestEnable);
        }
    }

    internal void Fill(in Box box, Rgba ink) {
        if (box.W <= 0 || box.H <= 0 || ink.A == 0) { return; }
        if (gpu) { TypeGlyphRenderer.Fill(box, ink); }
        else { batch.Draw(GUI.WhiteTexture, new Vector2(box.X, box.Y), null, new Color(ink.Packed), 0, Vector2.Zero, new Vector2(box.W, box.H), SpriteEffects.None, 0); }
    }

    internal void Frame(in Box box, float unit, Rgba ink) {
        Fill(new Box(box.X, box.Y, box.W, unit), ink);
        Fill(new Box(box.X, box.Bottom - unit, box.W, unit), ink);
        Fill(new Box(box.X, box.Y, unit, box.H), ink);
        Fill(new Box(box.Right - unit, box.Y, unit, box.H), ink);
    }

    internal void Draw(TextVisual visual, Vector2 position, float age, Color ink, float alpha = 1, float scale = 1, bool shadow = false, float richAlpha = -1) {
        if (!(alpha > 0) || !(age >= 0)) { return; }
        int shown = visual.Text.Revealed(age);
        int settled = Math.Min(shown, visual.Text.Revealed(age - visual.Text.FadeDuration));
        ReadOnlySpan<PreparedGlyph> glyphs = visual.Glyphs;
        int previous = -1;
        TypeGlyphMotion motion = default;
        Rgba color = default;
        float offsetX = 0, offsetY = 0;
        float elementX = 0, elementY = 0;
        for (int i = 0; i < glyphs.Length; i++) {
            ref readonly PreparedGlyph glyph = ref glyphs[i];
            int e = glyph.Element;
            if (e >= shown) { break; }
            if (e != previous) {
                previous = e;
                elementX = visual.Layout.X(e);
                elementY = visual.Layout.Line(e) * visual.Layout.LineHeight;
                motion = e >= settled ? visual.Text.Motion(e, age, visual.Layout.LineHeight) : default;
                ulong tint = visual.Text.Tint(e);
                Color styled = (tint & TypeRun.HasColor) == 0 ? ink : new Color((uint)tint);
                if ((tint & TypeRun.HasColor) != 0 && richAlpha >= 0) { styled *= richAlpha; }
                if ((tint & TypeRun.Metadata) != 0) { styled = Color.Lerp(styled, Color.White, 0.5f); }
                color = new Rgba(styled.PackedValue).Fade(alpha);
                if (!gpu && e >= settled) {
                    color = color.Fade(motion.Coverage);
                    float shift = motion.OffsetAt(motion.Phase - motion.Exposure * 0.5f);
                    offsetX = motion.X * shift; offsetY = motion.Y * shift;
                } else { offsetX = offsetY = 0; }
            }
            float x = position.X + (elementX + glyph.Offset + offsetX) * scale;
            float y = position.Y + (elementY + offsetY) * scale;
            ref readonly GlyphShape shape = ref visual.Face.Shape(glyph.Shape);
            if (shadow) { Submit(shape, x + scale, y + scale, scale, new Rgba(0, 0, 0, color.A), motion); }
            Submit(shape, x, y, scale, color, motion);
        }
    }

    private void Submit(in GlyphShape glyph, float x, float y, float scale, Rgba ink, in TypeGlyphMotion motion) {
        if (ink.A == 0 || glyph.Texture == null || glyph.Texture.IsDisposed) { return; }
        if (gpu) { TypeGlyphRenderer.Glyph(glyph, x, y, scale, ink, motion); }
        else {
            batch.Draw(glyph.Texture, new Vector2(x, y) + glyph.DrawOffset * scale, glyph.Source,
                new Color(ink.Packed), 0, Vector2.Zero, scale, SpriteEffects.None, 0);
        }
    }
}
