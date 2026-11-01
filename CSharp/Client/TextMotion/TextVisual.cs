using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PrimaryPackage.TextMotion;

internal sealed class GameFace : IFace {
    private static readonly ConditionalWeakTable<ScalableFont, GameFace> faces = new ConditionalWeakTable<ScalableFont, GameFace>();
    private readonly Dictionary<char, int> lookup = new Dictionary<char, int>();
    private GlyphShape[] shapes = new GlyphShape[64];
    private int count;
    private Texture2D generationTexture;
    internal readonly ScalableFont Font;
    internal readonly uint Size;
    internal readonly float LineHeight;
    internal GameFace(ScalableFont font) { Font = font; Size = font.Size; LineHeight = font.LineHeight; }
    internal bool Matches(ScalableFont font) => ReferenceEquals(Font, font) && Size == font.Size && LineHeight == font.LineHeight &&
        (generationTexture == null || !generationTexture.IsDisposed);
    internal static GameFace Of(ScalableFont font) {
        if (faces.TryGetValue(font, out GameFace face) && face.Matches(font)) { return face; }
        face = new GameFace(font); faces.Remove(font); faces.Add(font, face); return face;
    }
    internal static void Release() => faces.Clear();
    internal int Index(char codepoint) {
        if (lookup.TryGetValue(codepoint, out int index)) { return index; }
        var (data, texture) = Font.GetGlyphDataAndTextureForChar(codepoint);
        index = count;
        if (index == shapes.Length) { Array.Resize(ref shapes, shapes.Length * 2); }
        shapes[index] = new GlyphShape(texture, data);
        if (generationTexture == null && texture != null) { generationTexture = texture; }
        lookup.Add(codepoint, index); count++;
        return index;
    }
    public float Advance(int codepoint) {
        
        int index = Index((char)codepoint);
        return shapes[index].Advance;
    }
    internal ref readonly GlyphShape Shape(int index) => ref shapes[index];
}

internal readonly struct GlyphShape {
    internal readonly Texture2D Texture;
    internal readonly Vector2 DrawOffset;
    internal readonly Rectangle Source;
    internal readonly float Advance;
    internal GlyphShape(Texture2D texture, in ScalableFont.GlyphData data) {
        Texture = texture; DrawOffset = data.DrawOffset; Source = data.TexCoords; Advance = data.Advance;
    }
}

internal readonly struct PreparedGlyph {
    internal readonly int Shape, Element;
    internal readonly float Offset;
    internal PreparedGlyph(int shape, int element, float offset) { Shape = shape; Element = element; Offset = offset; }
}



internal sealed class TextVisual {
    internal const int MaxTextLength = TypeText.MaxLength;
    internal readonly TypeText Text;
    internal readonly TypeLayout Layout = new TypeLayout();
    internal GameFace Face { get; private set; }
    internal ReadOnlySpan<PreparedGlyph> Glyphs => glyphs.AsSpan(0, glyphCount);
    private PreparedGlyph[] glyphs = Array.Empty<PreparedGlyph>();
    private int glyphCount;
    private float width = -1;
    private int maxLines;
    private string wrapped;
    private bool prepared;
    private int suffixShape = -1, suffixElement;
    private float suffixX;
    private GUIFont sourceFont;
    private ScalableFont primary, selected;
    private uint primarySize;
    private int languageVersion;

    internal ScalableFont FontFor(GUIFont style) {
        ScalableFont value = style?.Value;
        if (value == null) { return null; }
        if (!ReferenceEquals(sourceFont, style) || !ReferenceEquals(primary, value) || primarySize != value.Size ||
            languageVersion != TextManager.LanguageVersion || selected == null) {
            ScalableFont next = style.GetFontForStr(Text.Plain);
            sourceFont = style; primary = value; primarySize = value.Size; languageVersion = TextManager.LanguageVersion;
            selected = next;
        }
        return selected;
    }

    internal TextVisual(string plain, in TypePace pace, IReadOnlyList<TypeRun> colors = null) {
        if (plain == null || plain.Length > MaxTextLength) { throw new ArgumentOutOfRangeException(nameof(plain)); }
        Text = TypeText.Parse(plain, pace, colors);
    }

    internal static List<TypeRun> Colors(ImmutableArray<RichTextData>? data) {
        if (!data.HasValue || data.Value.Length == 0) { return null; }
        var runs = new List<TypeRun>(data.Value.Length);
        foreach (RichTextData run in data.Value) {
            Color color = (run.Color ?? Color.Transparent) * run.Alpha;
            runs.Add(new TypeRun(run.StartIndex, run.EndIndex, color.PackedValue,
                run.Color.HasValue, !string.IsNullOrEmpty(run.Metadata)));
        }
        return runs;
    }

    internal static TextVisual Rich(string source) {
        RichString rich = RichString.Rich(source ?? "");
        return new TextVisual(rich.SanitizedValue, TypeSettings.Pace, Colors(rich.RichTextData));
    }

    internal void Wrap(ScalableFont font, float maxWidth, int lines) {
        bool newFace = Face == null || !Face.Matches(font);
        if (prepared && !newFace && width == maxWidth && maxLines == lines && wrapped == null) { return; }
        prepared = false;
        if (newFace) { Face = GameFace.Of(font); }
        width = maxWidth; maxLines = lines; wrapped = null;
        Layout.Wrap(Text, Face, maxWidth, Face.LineHeight, lines);
        suffixShape = -1;
        if (Layout.Truncated && Layout.Count > 0) {
            int shape = Face.Index('\u2026');
            float advance = Face.Shape(shape).Advance;
            if (advance > 0 && advance < maxWidth) {
                suffixShape = shape; suffixElement = Layout.Count - 1;
                suffixX = Layout.ReserveSuffix(advance, maxWidth);
            }
        }
        Prepare();
        prepared = true;
    }

    internal void Follow(ScalableFont font, string text) {
        bool newFace = Face == null || !Face.Matches(font);
        if (prepared && !newFace && string.Equals(wrapped, text, StringComparison.Ordinal)) { return; }
        prepared = false;
        if (newFace) { Face = GameFace.Of(font); }
        wrapped = text; width = -1;
        suffixShape = -1;
        Layout.Follow(Text, text, Face, Face.LineHeight);
        Prepare();
        prepared = true;
    }

    private void Prepare() {
        int capacity = (Layout.Count == 0 ? 0 : Text.End(Layout.Count - 1)) + (suffixShape >= 0 ? 1 : 0);
        if (glyphs.Length < capacity) { glyphs = new PreparedGlyph[capacity]; }
        int count = 0;
        for (int e = 0; e < Layout.Count; e++) {
            foreach (ref readonly TypeGlyph glyph in Layout.Glyphs(e)) {
                int shape = Face.Index((char)glyph.Codepoint);
                ref readonly GlyphShape data = ref Face.Shape(shape);
                if (data.Texture == null || data.Source.Width <= 0 || char.IsWhiteSpace((char)glyph.Codepoint)) { continue; }
                glyphs[count++] = new PreparedGlyph(shape, e, glyph.Offset);
            }
        }
        if (suffixShape >= 0) { glyphs[count++] = new PreparedGlyph(suffixShape, suffixElement, suffixX - Layout.X(suffixElement)); }
        glyphCount = count;
    }
}
