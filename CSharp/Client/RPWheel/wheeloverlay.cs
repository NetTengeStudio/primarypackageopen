#nullable enable
using System;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.RPWheel;
#if CLIENT
using GUI = global::Barotrauma.GUI;
#endif

internal sealed class WheelOverlay : IDisposable {
    private readonly WheelModel model;
    private readonly WheelRenderer renderer;
    private readonly WheelArc circle = new(0, MathF.Tau - 0.001f);
    private readonly WheelArc[] arcs;
    private readonly Vector2[] midpoints;
    private readonly string[] labels;
    private readonly Vector2[] sizes;
    private readonly float[] hover;
    private readonly Vector2[] vignette = new Vector2[33];
    private readonly VertexPositionColor[] vignetteStrip = new VertexPositionColor[66];
    private BasicEffect? effect;
    private ScalableFont? measuredFont;
    private LanguageIdentifier language;
    private readonly EmoteDef[] definitions;
    private Matrix projection;
    private Vector2 center;
    private int width, height, geometryCount;
    private float scale = -1, alpha = -1, uiScale = -1, closeHover = -1;

    public WheelOverlay(EmoteDef[] definitions, WheelModel model) {
        if (definitions.Length < model.Count) { throw new ArgumentException(nameof(definitions)); }
        this.model = model;
        this.definitions = definitions;
        arcs = new WheelArc[model.Count];
        midpoints = new Vector2[model.Count];
        labels = new string[model.Count];
        sizes = new Vector2[model.Count];
        hover = new float[model.Count];
        int points = 0;
        for (int i = 0; i < model.Count; i++) {
            float from = model.SectorAngle * i + MathF.PI * 0.5f;
            float to = model.SectorAngle * (i + 1) + MathF.PI * 0.5f;
            arcs[i] = new WheelArc(from, to);
            float mid = from + model.SectorAngle * 0.5f;
            midpoints[i] = new Vector2(MathF.Cos(mid), -MathF.Sin(mid));
            points += arcs[i].Units.Length;
        }
        renderer = new WheelRenderer(24 * points + 12 * circle.Units.Length + 42 * model.Count + 320);
        for (int i = 0; i < vignette.Length; i++) {
            float angle = MathF.Tau * i / 32;
            vignette[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
    }

    public void Dispose() {
        renderer.Dispose();
        effect?.Dispose();
        effect = null;
        measuredFont = null;
    }

    public void Draw(SpriteBatch batch, double now, Vector2 pointer) {
        model.Advance(now);
        if (!model.Visible || GameMain.GraphicsWidth <= 0 || GameMain.GraphicsHeight <= 0) { return; }
        GraphicsDevice device = batch.GraphicsDevice;
        if (effect == null || effect.IsDisposed || effect.GraphicsDevice != device) {
            effect?.Dispose();
            effect = new BasicEffect(device) { VertexColorEnabled = true, World = Matrix.Identity, View = Matrix.Identity };
        }
        if (UpdateFrame()) {
            renderer.Rewind(0);
            Geometry();
            geometryCount = renderer.Count;
        } else { renderer.Rewind(geometryCount); }
        Cursor(pointer);
        var rasterizer = device.RasterizerState;
        var blend = device.BlendState;
        var depth = device.DepthStencilState;
        try {
            device.RasterizerState = RasterizerState.CullNone;
            device.BlendState = BlendState.NonPremultiplied;
            device.DepthStencilState = DepthStencilState.None;
            effect.Projection = projection;
            foreach (var pass in effect.CurrentTechnique.Passes) {
                pass.Apply();
                renderer.Flush(device);
            }
        } finally {
            device.RasterizerState = rasterizer;
            device.BlendState = blend;
            device.DepthStencilState = depth;
        }
        batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied,
            samplerState: GUI.SamplerState, rasterizerState: GameMain.ScissorTestEnable);
        try { Labels(batch); }
        finally { batch.End(); }
    }

    private bool UpdateFrame() {
        bool dirty = false;
        if (width != GameMain.GraphicsWidth || height != GameMain.GraphicsHeight) {
            width = GameMain.GraphicsWidth;
            height = GameMain.GraphicsHeight;
            center = new Vector2(width * 0.5f, height * 0.5f);
            projection = Matrix.CreateOrthographicOffCenter(0, width, height, 0, 0, 1);
            dirty = true;
        }
        float nextScale = model.Scale, nextAlpha = model.Alpha, nextCenter = model.Center;
        dirty |= nextScale != scale || nextAlpha != alpha || nextCenter != closeHover || uiScale != GUI.Scale;
        scale = nextScale;
        alpha = nextAlpha;
        closeHover = nextCenter;
        uiScale = GUI.Scale;
        for (int i = 0; i < hover.Length; i++) {
            float next = model.Hover(i);
            dirty |= next != hover[i];
            hover[i] = next;
        }
        return dirty;
    }

    private void Geometry() {
        Vignette();
        float currentScale = scale * uiScale;
        float inner = model.InnerRadius * currentScale, outer = model.OuterRadius * currentScale;
        for (int i = 0; i < arcs.Length; i++) { renderer.Glow(center, inner, outer, arcs[i], hover[i], alpha); }
        for (int i = 0; i < arcs.Length; i++) {
            Color fill = Fade(Color.Lerp(Theme.ButtonDefault, Theme.ButtonHovered, hover[i]), alpha);
            renderer.Annulus(center, MathF.Max(0, inner - 2 * currentScale), outer + 2 * currentScale, arcs[i], fill);
        }
        renderer.Annulus(center, 0, inner, circle, Fade(Theme.PanelDark, alpha));
        for (int i = 0; i < arcs.Length; i++) {
            Vector2 direction = arcs[i].Units[0];
            renderer.Line(center + direction * inner, center + direction * outer,
                Theme.SeparatorThickness * uiScale * 0.5f, Fade(Theme.Separator, alpha));
        }
        for (int i = 0; i < arcs.Length; i++) {
            Color border = Fade(Color.Lerp(Theme.BorderDefault, Theme.BorderHover, hover[i]), alpha);
            renderer.Border(center, outer, arcs[i], border, Theme.BorderThickness * uiScale);
            renderer.Border(center, inner, arcs[i], border, Theme.BorderThickness * uiScale);
        }
        float h = inner * Theme.CrossSize, lineWidth = inner * Theme.CrossLineWidth;
        Color cross = Fade(Color.Lerp(Theme.CloseIdle, Theme.CloseHover, closeHover), alpha);
        renderer.Line(center + new Vector2(-h, -h), center + new Vector2(h, h), lineWidth, cross);
        renderer.Line(center + new Vector2(h, -h), center + new Vector2(-h, h), lineWidth, cross);
    }

    private void Vignette() {
        float radius = MathF.Sqrt((float)width * width + (float)height * height) * 0.5f + 16;
        byte maxAlpha = (byte)(245 * alpha);
        for (int ring = 0; ring < 4; ring++) {
            float x0 = ring / 4f, x1 = (ring + 1) / 4f;
            Color c0 = new(0, 0, 0, (int)(maxAlpha * MathF.Pow(x0, 2.5f)));
            Color c1 = new(0, 0, 0, (int)(maxAlpha * MathF.Pow(x1, 2.5f)));
            for (int i = 0; i < vignette.Length; i++) {
                vignetteStrip[i * 2] = new VertexPositionColor(new Vector3(center + vignette[i] * (x1 * radius), 0), c1);
                vignetteStrip[i * 2 + 1] = new VertexPositionColor(new Vector3(center + vignette[i] * (x0 * radius), 0), c0);
            }
            renderer.Strip(vignetteStrip);
        }
    }

    private void Cursor(Vector2 pointer) {
        renderer.Annulus(pointer + new Vector2(1.5f * uiScale), 0, 5.5f * uiScale, circle,
            new Color(0, 0, 0, (int)(255 * alpha * 0.55f)), 2 * uiScale);
        renderer.Annulus(pointer, 0, 4 * uiScale, circle,
            new Color(255, 255, 255, (int)(255 * alpha)), 1.5f * uiScale);
    }

    private void Labels(SpriteBatch batch) {
        ScalableFont? font = Theme.LabelFont ?? GUIStyle.SmallFont?.Value;
        if (font == null) { return; }
        var currentLanguage = GameSettings.CurrentConfig.Language;
        if (measuredFont != font || language != currentLanguage) {
            for (int i = 0; i < labels.Length; i++) {
                labels[i] = TextManager.Get(definitions[i].NameKey).Value;
                sizes[i] = font.MeasureString(labels[i]);
            }
            measuredFont = font;
            language = currentLanguage;
        }
        float inner = model.InnerRadius * scale * uiScale, outer = model.OuterRadius * scale * uiScale;
        float radius = (inner + outer) * 0.5f, maxWidth = (outer - inner) * 0.85f;
        for (int i = 0; i < labels.Length; i++) {
            float textScale = sizes[i].X * uiScale > maxWidth && maxWidth > 0
                ? maxWidth / sizes[i].X : uiScale;
            Vector2 position = center + midpoints[i] * radius - new Vector2(0, sizes[i].Y * textScale * 0.5f);
            font.DrawString(batch, labels[i], position + new Vector2(uiScale), Fade(Theme.TextShadow, alpha),
                0, Vector2.Zero, textScale, SpriteEffects.None, 0.09f, alignment: Alignment.CenterX);
            font.DrawString(batch, labels[i], position, Fade(Color.Lerp(Theme.NanoGold, Theme.NanoGoldBright, hover[i]), alpha),
                0, Vector2.Zero, textScale, SpriteEffects.None, 0.10f, alignment: Alignment.CenterX);
        }
    }

    private static Color Fade(Color color, float opacity) => new(color.R, color.G, color.B, (int)(color.A * opacity));
}
