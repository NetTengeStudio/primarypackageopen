using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.GUI;

public static class GUITheme {
    public static readonly Color Panel = new(0, 0, 0, 236);
    public static readonly Color Control = new(12, 12, 12);
    public static readonly Color Hover = new(30, 30, 30);
    public static readonly Color Pressed = new(48, 48, 48);
    public static readonly Color Border = new(105, 105, 105);
    public static readonly Color Text = new(200, 200, 200);
    public static readonly Color Muted = new(125, 125, 125);
    public static readonly Color Rule = new(60, 60, 60);
    public static readonly Color EdgeLit = new(160, 160, 160);
    public static readonly Color Accent = new(232, 176, 72);
    public const float Padding = 6, Gap = 1, Edge = 3, TitleHeight = 22, RowHeight = 22, ButtonHeight = 24;
    public const float FontSize = 14, LineHeight = 18;
}

internal readonly struct GUIDrawCommand {
    public readonly Rectangle Bounds, Clip;
    public readonly Color Color;
    public readonly string Text;
    public readonly bool Border, TitleFont;
    public readonly CharacterInfo Preview;
    public readonly Sprite Sprite;
    public readonly float TextScale;
    public GUIDrawCommand(Rectangle bounds, Rectangle clip, Color color, string text = null, bool border = false, float textScale = 1, CharacterInfo preview = null, bool titleFont = false, Sprite sprite = null) {
        Bounds = bounds; Clip = clip; Color = color; Text = text; Border = border; TextScale = textScale; Preview = preview; TitleFont = titleFont; Sprite = sprite;
    }
}

public sealed class GUIRenderer {
    private List<GUIDrawCommand> commands;
    private readonly List<GUIDrawCommand> scratch = new(128);
    private Rectangle clip;
    private float scale;
    internal float Scale => scale;
    private ScalableFont font;
    private ScalableFont heading;
    private int fontGeneration = -1;
    internal int TextGeneration { get; private set; }
    private readonly ScalableFont suppliedFont;
    private readonly ScalableFont suppliedHeading;
    private readonly Dictionary<string, Vector2> measurements = new(StringComparer.Ordinal);
    public long RebuiltWindows { get; private set; }
    public long SubmittedCommands { get; private set; }
    public int LastBatches { get; private set; }
    public GUIRenderer(ScalableFont font = null) { suppliedFont = font; }
    public GUIRenderer(ScalableFont font, ScalableFont heading) { suppliedFont = font; suppliedHeading = heading; }
    internal void InvalidateText() { measurements.Clear(); scratch.Clear(); font = null; }

    internal bool Prepare(float uiScale) {
        if (font != null && fontGeneration == Fonts.FontRegistry.Generation && scale == uiScale) { return false; }
        var nextFont = suppliedFont ?? Fonts.FontRegistry.Get("departure_mono".ToIdentifier(), 14, true) ?? GUIStyle.SmallFont?.Value;
        var nextHeading = suppliedHeading ?? suppliedFont ?? Fonts.FontRegistry.Get("scpitd_header".ToIdentifier(), 14, true) ?? nextFont;
        bool changed = font != nextFont || heading != nextHeading || scale != uiScale;
        fontGeneration = Fonts.FontRegistry.Generation;
        if (changed) { measurements.Clear(); font = nextFont; heading = nextHeading; scale = uiScale; TextGeneration++; }
        return changed;
    }
    internal void Build(GUIWindow window, float uiScale) {
        if (Prepare(uiScale)) { window.RebuildCommands = true; }
        if (!window.RebuildCommands && Patch(window)) { return; }
        commands = window.Commands;
        commands.Clear();
        clip = window.Clip;
        float alpha = 1;
        Fill(window.Bounds, GUITheme.Panel, alpha);
        Border(window.Bounds, GUITheme.Border, alpha);
        int edge = Math.Max(1, (int)MathF.Round(GUITheme.Edge * scale));
        Rectangle title = new(window.Bounds.X + edge, window.Bounds.Y + edge, Math.Max(0, window.Bounds.Width - edge * 2), (int)(GUITheme.TitleHeight * scale));
        Fill(title, GUITheme.Border, alpha);
        Write(window.Title, new Rectangle(title.X + (int)(6 * scale), title.Y, Math.Max(0, title.Width - (int)(36 * scale)), title.Height), Color.Black, alpha, titleFont: true);
        Write("×", new Rectangle(title.Right - (int)(22 * scale), title.Y, (int)(20 * scale), title.Height), Color.Black, alpha, centered: true, titleFont: true);
        PaintChildren(window, alpha);
        clip = window.Clip;
        window.Paint(this, alpha);
        window.RebuildCommands = false;
        ClearDirty(window);
        window.WindowDirty = GUIDirty.None;
        RebuiltWindows++;
    }

    private bool Patch(GUIWindow window) {
        for (int i = 0; i < window.DirtyNodes.Count; i++) {
            GUIComponent node = window.DirtyNodes[i];
            if (node == window || node.Disposed || node.RenderStart < 0) { continue; }
            commands = scratch;
            scratch.Clear();
            clip = node is VirtualList or Transcript ? node.Clip : node.Parent?.Clip ?? node.Clip;
            node.Paint(this, 1);
            if (scratch.Count != node.RenderCount) { return false; }
            for (int c = 0; c < scratch.Count; c++) { window.Commands[node.RenderStart + c] = scratch[c]; }
            node.Dirty = GUIDirty.None;
        }
        ClearDirty(window);
        window.WindowDirty = GUIDirty.None;
        return true;
    }
    private static void ClearDirty(GUIWindow window) {
        for (int i = 0; i < window.DirtyNodes.Count; i++) { window.DirtyNodes[i].Queued = false; }
        window.DirtyNodes.Clear();
    }

    private void PaintChildren(GUIComponent parent, float opacity) {
        for (int i = 0; i < parent.Children.Count; i++) {
            GUIComponent child = parent.Children[i];
            if (!child.Visible || child.Disposed || child.Clip.Width <= 0 || child.Clip.Height <= 0) { ClearRanges(child); continue; }
            clip = child is VirtualList or Transcript ? child.Clip : parent.Clip;
            child.RenderStart = commands.Count;
            child.Paint(this, opacity);
            child.RenderCount = commands.Count - child.RenderStart;
            child.Dirty = GUIDirty.None;
            PaintChildren(child, opacity);
        }
    }
    private static void ClearRanges(GUIComponent node) {
        node.RenderStart = -1;
        for (int i = 0; i < node.Children.Count; i++) { ClearRanges(node.Children[i]); }
    }

    internal void Fill(Rectangle bounds, Color color, float opacity) => commands.Add(new(bounds, clip, Fade(color, opacity)));
    internal void Image(Sprite sprite, Rectangle bounds, float opacity) => commands.Add(new(bounds, clip, Fade(Color.White, opacity), sprite: sprite));
    internal void Preview(CharacterInfo info, Rectangle bounds, float opacity) => commands.Add(new(bounds, clip, Fade(Color.White, opacity), preview: info));
    internal void Preview(CharacterPreview preview, float opacity) => commands.Add(new(preview.Bounds, clip, Fade(Color.White, opacity), preview: preview.CharacterInfo));
    internal void Border(Rectangle bounds, Color color, float opacity) => commands.Add(new(bounds, clip, Fade(color, opacity), border: true));
    internal void Text(GUIComponent component, Color color, float opacity, bool centered = false) {
        string value = component is TextInput input ? input.DisplayText : component.Text;
        component.TextSize = Measure(value);
        var bounds = component.Bounds;
        bounds.X += (int)(6 * scale);
        bounds.Width = Math.Max(0, bounds.Width - (int)(12 * scale));
        if (component is Text { Wrap: true }) { Paragraph(value, bounds, color, opacity); }
        else { Write(value, bounds, color, opacity, centered); }
    }
    internal void InputText(TextInput input, float opacity) {
        clip = Rectangle.Intersect(clip, input.Bounds);
        int padding = Math.Max(1, (int)(6 * scale));
        string value = input.Text.Length == 0 && !input.Selected ? input.Placeholder : input.DisplayText;
        float caret = Measure(input.Text.Substring(0, input.Caret)).X * scale;
        float offset = Math.Max(0, caret - Math.Max(0, input.Bounds.Width - padding * 2 - 2));
        float height = MathF.Ceiling(GUITheme.FontSize * scale * 1.25f);
        int y = input.Bounds.Y + Math.Max(0, (int)((input.Bounds.Height - height) * .5f));
        if (input.Selected && input.SelectionLength > 0) {
            float start = Measure(input.Text.Substring(0, input.SelectionStart)).X * scale;
            float end = Measure(input.Text.Substring(0, input.SelectionStart + input.SelectionLength)).X * scale;
            Fill(new Rectangle(input.Bounds.X + padding + (int)(start - offset), y, (int)Math.Ceiling(end - start), Math.Max(1, (int)height)), GUITheme.Pressed, opacity);
        }
        commands.Add(new(new Rectangle(input.Bounds.X + padding - (int)offset, y, 0, 0), clip, Fade(input.Text.Length == 0 ? GUITheme.Muted : GUITheme.Text, opacity), value, textScale: scale));
        if (input.Selected) { Fill(new Rectangle(input.Bounds.X + padding + (int)(caret - offset), y, Math.Max(1, (int)scale), Math.Max(1, (int)height)), GUITheme.Accent, opacity); }
    }
    internal int TextIndex(TextInput input, int x) {
        float caret = Measure(input.Text.Substring(0, input.Caret)).X * scale;
        float offset = Math.Max(0, caret - Math.Max(0, input.Bounds.Width - 12 * scale - 2));
        float target = x - input.Bounds.X - 6 * scale + offset;
        int low = 0, high = input.Text.Length;
        while (low < high) {
            int middle = (low + high + 1) / 2;
            if (Measure(input.Text.Substring(0, middle)).X * scale <= target) { low = middle; }
            else { high = middle - 1; }
        }
        if (low > 0 && low < input.Text.Length && char.IsLowSurrogate(input.Text[low])) { low--; }
        return low;
    }
    private void Paragraph(string value, Rectangle bounds, Color color, float opacity) {
        clip = Rectangle.Intersect(clip, bounds);
        int lineHeight = Math.Max(1, (int)MathF.Ceiling(GUITheme.FontSize * scale * 1.25f));
        int start = 0, y = bounds.Y;
        while (start < value.Length && y < bounds.Bottom) {
            int low = 1, high = value.Length - start, count = 1;
            while (low <= high) {
                int middle = (low + high) / 2;
                if (Measure(value.Substring(start, middle)).X * scale <= bounds.Width) { count = middle; low = middle + 1; }
                else { high = middle - 1; }
            }
            if (start + count < value.Length) {
                int space = value.LastIndexOf(' ', start + count - 1, count);
                if (space >= start) { count = Math.Max(1, space - start); }
            }
            commands.Add(new(new Rectangle(bounds.X, y, 0, 0), clip, Fade(color, opacity), value.Substring(start, count), textScale: scale));
            start += count;
            while (start < value.Length && value[start] == ' ') { start++; }
            y += lineHeight;
        }
    }
    internal void Write(string value, Rectangle bounds, Color color, float opacity, bool centered = false, bool titleFont = false) {
        if (value.Length == 0 || font == null) { return; }
        ScalableFont face = titleFont ? heading : font;
        Vector2 size = titleFont ? face.MeasureString(value) : Measure(value);
        float textScale = scale;
        if (size.X * textScale > bounds.Width) {
            float available = Math.Max(0, bounds.Width / textScale - face.GetGlyphDataAndTextureForChar('.').GlyphData.Advance * 3);
            float advance = 0;
            int count = 0;
            while (count < value.Length) {
                int length = char.IsHighSurrogate(value[count]) && count + 1 < value.Length && char.IsLowSurrogate(value[count + 1]) ? 2 : 1;
                float next = face.GetGlyphDataAndTextureForChar(length == 1 ? value[count] : '?').GlyphData.Advance;
                if (advance + next > available) { break; }
                advance += next; count += length;
            }
            value = string.Concat(value.AsSpan(0, count), "...");
            centered = false;
        }
        float lineHeight = MathF.Ceiling(GUITheme.FontSize * textScale * 1.25f);
        Rectangle textClip = lineHeight > bounds.Height ? Rectangle.Intersect(clip, bounds) : clip;
        var at = new Rectangle((int)MathF.Round(bounds.X + (centered ? Math.Max(0, (bounds.Width - size.X * textScale) * .5f) : 0)), (int)MathF.Round(bounds.Y + Math.Max(0, (bounds.Height - lineHeight) * .5f)), 0, 0);
        commands.Add(new(at, textClip, Fade(color, opacity), value, textScale: textScale, titleFont: titleFont));
    }
    internal Vector2 Measure(string value) {
        if (font == null) { return Vector2.Zero; }
        if (measurements.TryGetValue(value, out var size)) { return size; }
        if (measurements.Count >= 4096) { measurements.Clear(); }
        size = font.MeasureString(value);
        measurements.Add(value, size);
        return size;
    }

    internal void Draw(SpriteBatch batch, IReadOnlyList<GUIWindow> windows) {
        if (windows.Count == 0) { return; }
        GraphicsDevice device = batch.GraphicsDevice;
        Rectangle previousClip = device.ScissorRectangle;
        batch.End();
        bool begun = false;
        Rectangle current = Rectangle.Empty;
        LastBatches = 0;
        try {
            for (int w = 0; w < windows.Count; w++) {
                GUIWindow window = windows[w];
                float opacity = (float)Math.Clamp(window.Openness.Position, 0, 1);
                if (opacity <= 0) { continue; }
                if (begun) { batch.End(); begun = false; }
                Matrix transform = Matrix.CreateTranslation(0, (1 - opacity) * 8 * scale, 0);
                var list = window.Commands;
                for (int i = 0; i < list.Count; i++) {
                    ref readonly var command = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)[i];
                    if (command.Clip.Width <= 0 || command.Clip.Height <= 0) { continue; }
                    Vector2 clipStart = Vector2.Transform(command.Clip.Location.ToVector2(), transform);
                    Vector2 clipEnd = Vector2.Transform(new Vector2(command.Clip.Right, command.Clip.Bottom), transform);
                    Rectangle nextClip = Rectangle.Intersect(device.Viewport.Bounds,
                        new Rectangle((int)Math.Floor(clipStart.X), (int)Math.Floor(clipStart.Y), Math.Max(0, (int)Math.Ceiling(clipEnd.X - clipStart.X)), Math.Max(0, (int)Math.Ceiling(clipEnd.Y - clipStart.Y))));
                    if (nextClip.Width <= 0 || nextClip.Height <= 0) { continue; }
                    if (!begun || current != nextClip) {
                        if (begun) { batch.End(); }
                        current = nextClip;
                        device.ScissorRectangle = current;
                        batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, Barotrauma.GUI.SamplerState,
                            DepthStencilState.None, GameMain.ScissorTestEnable, transformMatrix: transform);
                        begun = true;
                        LastBatches++;
                    }
                    if (command.Sprite != null) {
                        var sprite = command.Sprite;
                        float fit = Math.Min(command.Bounds.Width / Math.Max(1f, sprite.size.X), command.Bounds.Height / Math.Max(1f, sprite.size.Y));
                        sprite.Draw(batch, command.Bounds.Center.ToVector2(), Fade(command.Color, opacity), sprite.size * .5f, scale: fit);
                    } else if (command.Preview != null) {
                        command.Preview.DrawIcon(batch, command.Bounds.Center.ToVector2(), command.Bounds.Size.ToVector2());
                    } else if (command.Text != null) {
                        (command.TitleFont ? heading : font)?.DrawString(batch, command.Text, new Vector2(command.Bounds.X, command.Bounds.Y), Fade(command.Color, opacity),
                            0, Vector2.Zero, command.TextScale, SpriteEffects.None, 0);
                    } else {
                        Barotrauma.GUI.DrawRectangle(batch, command.Bounds, Fade(command.Color, opacity), !command.Border, thickness: Math.Max(1, MathF.Round(GUITheme.Edge * scale)));
                    }
                    SubmittedCommands++;
                }
            }
        } finally {
            if (begun) { batch.End(); }
            device.ScissorRectangle = previousClip;
            batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, Barotrauma.GUI.SamplerState,
                DepthStencilState.None, GameMain.ScissorTestEnable);
        }
    }

    private static Color Fade(Color color, float opacity) => new(color.R, color.G, color.B, (byte)(color.A * opacity));
}
