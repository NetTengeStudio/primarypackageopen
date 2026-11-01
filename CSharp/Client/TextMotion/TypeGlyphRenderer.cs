using System;
using System.IO;
using System.Runtime.InteropServices;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PrimaryPackage.Core;
using PrimaryPackage.TextMotion;

namespace PrimaryPackage.TextMotion {
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif
    internal static class TypeGlyphRenderer {
        private const int Capacity = 512;

        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex : IVertexType {
            public Vector2 Position;
            public Color Color;
            public Vector2 Uv;
            public Vector4 Bounds;
            public Vector4 Motion;

            public static VertexDeclaration Declaration => declaration ??= new VertexDeclaration(
                new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0),
                new VertexElement(8, VertexElementFormat.Color, VertexElementUsage.Color, 0),
                new VertexElement(12, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
                new VertexElement(20, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 1),
                new VertexElement(36, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 2));

            VertexDeclaration IVertexType.VertexDeclaration => Declaration;
        }

        private static readonly Vertex[] vertices = new Vertex[Capacity * 4];
        private static VertexDeclaration declaration;
        private static readonly short[] indices = MakeIndices();
        private static Effect effect;
        private static EffectParameter glyphParameter;
        private static EffectParameter sizeParameter;
        private static GraphicsDevice graphics;
        private static Texture2D atlas;
        private static int count;
        private static bool unavailable;
        private static BlendState savedBlend;
        private static DepthStencilState savedDepth;
        private static RasterizerState savedRasterizer;
        private static SamplerState savedSampler;
        private static Texture savedTexture;
        private static Vector2 clipScale;
        private static Vector2 clipOffset;

        public static bool Ready(GraphicsDevice device) {
            if (unavailable || device == null) { return false; }
            if (effect != null && !effect.IsDisposed && ReferenceEquals(effect.GraphicsDevice, device)) { return true; }
            effect?.Dispose();
            declaration?.Dispose(); declaration = null;
            effect = null;
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string path = "Content/Effects/Shaders/typeglyph_" + (windows ? "dx11" : "opengl") + ".mgfxo";
            try {
                byte[] bytes = File.ReadAllBytes(ModAssets.Resolve(path));
                if (bytes.Length < 14 || BitConverter.ToInt32(bytes, 0) != 0x5846474D || bytes[4] != 8 || bytes[5] != (windows ? 1 : 0)) {
                    throw new InvalidDataException("MGFX 8 backend does not match the installed renderer");
                }
                effect = new Effect(device, bytes);
                glyphParameter = effect.Parameters["xGlyph"];
                sizeParameter = effect.Parameters["xAtlasSize"];
                EffectTechnique technique = effect.Techniques["GlyphMotion"];
                if (glyphParameter == null || sizeParameter == null || technique == null) { throw new InvalidDataException("glyph shader interface is incomplete"); }
                effect.CurrentTechnique = technique;
                return true;
            } catch (Exception error) {
                effect?.Dispose();
                effect = null;
                unavailable = true;
                ModLog.Error($"{path}: glyph motion blur unavailable; text uses a sharp fallback: {error.Message}");
                return false;
            }
        }

        public static void Begin(GraphicsDevice device) {
            graphics = device;
            savedBlend = device.BlendState;
            savedDepth = device.DepthStencilState;
            savedRasterizer = device.RasterizerState;
            savedSampler = device.SamplerStates[0];
            savedTexture = device.Textures[0];
            Viewport viewport = device.Viewport;
            clipScale = new Vector2(2.0f / viewport.Width, -2.0f / viewport.Height);
            clipOffset = new Vector2(-1.0f - viewport.X * clipScale.X, 1.0f - viewport.Y * clipScale.Y);
            device.BlendState = BlendState.NonPremultiplied;
            device.DepthStencilState = DepthStencilState.None;
            device.RasterizerState = GameMain.ScissorTestEnable;
            device.SamplerStates[0] = GUI.SamplerState;
            count = 0;
            atlas = null;
        }

        public static void End() {
            if (graphics == null) { return; }
            try { Flush(); }
            finally {
                graphics.BlendState = savedBlend;
                graphics.DepthStencilState = savedDepth;
                graphics.RasterizerState = savedRasterizer;
                graphics.SamplerStates[0] = savedSampler;
                graphics.Textures[0] = savedTexture;
                graphics = null;
                atlas = null;
                savedTexture = null;
                count = 0;
            }
        }

        public static void Fill(in Box box, Rgba color) =>
            Quad(GUI.WhiteTexture, box.X, box.Y, box.Right, box.Bottom, new Vector4(0, 0, 1, 1), new Vector4(0, 0, 1, 1), default, color);

        public static void FlushBeforeClip() => Flush();

        public static void Glyph(in GlyphShape glyph, float x, float y, float scale, Rgba color, in TypeGlyphMotion motion) {
            Texture2D texture = glyph.Texture;
            Rectangle rect = glyph.Source;
            if (texture == null || texture.IsDisposed || rect.Width <= 0 || rect.Height <= 0 || color.A == 0) { return; }
            x += glyph.DrawOffset.X * scale;
            y += glyph.DrawOffset.Y * scale;
            float invWidth = 1.0f / texture.Width;
            float invHeight = 1.0f / texture.Height;
            var bounds = new Vector4(rect.Left * invWidth, rect.Top * invHeight, rect.Right * invWidth, rect.Bottom * invHeight);
            if (motion.Exposure <= 0.0f) {
                Quad(texture, x, y, x + rect.Width * scale, y + rect.Height * scale, bounds, bounds, default, color);
                return;
            }
            float left = MathF.Min(0.0f, motion.X) - 1.0f;
            float top = MathF.Min(0.0f, motion.Y) - 1.0f;
            float right = rect.Width + MathF.Max(0.0f, motion.X) + 1.0f;
            float bottom = rect.Height + MathF.Max(0.0f, motion.Y) + 1.0f;
            var uv = new Vector4((rect.Left + left) * invWidth, (rect.Top + top) * invHeight,
                (rect.Left + right) * invWidth, (rect.Top + bottom) * invHeight);
            var sweep = new Vector4(motion.X * invWidth, motion.Y * invHeight, motion.Phase, motion.Exposure);
            Quad(texture, x + left * scale, y + top * scale, x + right * scale, y + bottom * scale, uv, bounds, sweep, color);
        }

        private static void Quad(Texture2D texture, float left, float top, float right, float bottom,
            in Vector4 uv, in Vector4 bounds, in Vector4 motion, Rgba color) {
            if (count == Capacity || !ReferenceEquals(atlas, texture)) { Flush(); atlas = texture; }
            int index = count++ * 4;
            Color ink = new Color(color.Packed);
            vertices[index] = Make(left, top, uv.X, uv.Y, ink, bounds, motion);
            vertices[index + 1] = Make(right, top, uv.Z, uv.Y, ink, bounds, motion);
            vertices[index + 2] = Make(right, bottom, uv.Z, uv.W, ink, bounds, motion);
            vertices[index + 3] = Make(left, bottom, uv.X, uv.W, ink, bounds, motion);
        }

        private static Vertex Make(float x, float y, float u, float v, Color color, in Vector4 bounds, in Vector4 motion) => new Vertex {
            Position = new Vector2(x * clipScale.X + clipOffset.X, y * clipScale.Y + clipOffset.Y),
            Color = color,
            Uv = new Vector2(u, v),
            Bounds = bounds,
            Motion = motion
        };

        private static void Flush() {
            if (count == 0) { return; }
            glyphParameter.SetValue(atlas);
            sizeParameter.SetValue(new Vector2(atlas.Width, atlas.Height));
            effect.CurrentTechnique.Passes[0].Apply();
            
            
            graphics.SamplerStates[0] = GUI.SamplerState;
            graphics.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, vertices, 0, count * 4, indices, 0, count * 2);
            count = 0;
        }

        private static short[] MakeIndices() {
            var result = new short[Capacity * 6];
            for (int i = 0; i < Capacity; i++) {
                int b = i * 4;
                int at = i * 6;
                result[at] = (short)b;
                result[at + 1] = (short)(b + 1);
                result[at + 2] = (short)(b + 2);
                result[at + 3] = (short)b;
                result[at + 4] = (short)(b + 2);
                result[at + 5] = (short)(b + 3);
            }
            return result;
        }

        public static void Release() {
            effect?.Dispose();
            declaration?.Dispose();
            declaration = null;
            effect = null;
            glyphParameter = null;
            sizeParameter = null;
            atlas = null;
            graphics = null;
            savedBlend = null;
            savedDepth = null;
            savedRasterizer = null;
            savedSampler = null;
            savedTexture = null;
            unavailable = false;
            count = 0;
        }
    }
}
