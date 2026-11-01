using System;
using System.Collections.Generic;
using System.Globalization;

namespace PrimaryPackage.TextMotion {
    public struct TypePace {
        public const float BaseCps = 55.0f;
        public const float SpaceShare = 0.6f;
        public const float DotSeconds = 0.075f;
        public const float CommaPause = 0.09f;
        public const float StopPause = 0.22f;
        public const float EllipsisPause = 0.34f;
        public const float TwoDotPause = 0.26f;
        public const float BreakPause = 0.14f;
        public const float PauseCap = 0.4f;
        public const float FadeSeconds = 0.14f;
        public const float ShortSeconds = 0.36f;
        public const float ShortIncrement = 0.018f;
        public const int ShortElements = 12;
        public const float CompressFrom = 1.2f;
        public const float MaxSeconds = 2.4f;
        public const float MinSpeed = 0.25f;
        public const float MaxSpeed = 4.0f;

        public float Speed;
        public bool Instant;

        public static TypePace Normal => new TypePace { Speed = 1.0f };

        public float Char => 1.0f / (BaseCps * Clamped);

        public float Clamped => float.IsNaN(Speed) ? 1.0f : Speed < MinSpeed ? MinSpeed : Speed > MaxSpeed ? MaxSpeed : Speed;
    }

    public readonly struct TypeRun {
        public const ulong HasColor = 1UL << 32;
        public const ulong Metadata = 1UL << 33;
        public readonly int From;
        public readonly int To;
        public readonly uint Color;
        public readonly ulong Flags;

        public TypeRun(int from, int to, uint color, bool hasColor = true, bool metadata = false) {
            From = from;
            To = to;
            Color = color;
            Flags = (hasColor ? HasColor : 0) | (metadata ? Metadata : 0);
        }
    }

    public sealed class TypeText {
        public const int MaxLength = 16384;
        private readonly int[] start;
        private readonly float[] at;
        private readonly ulong[] tint;
        private readonly float inverseMove;
        private readonly float exposure;

        private TypeText(string plain, int[] start, float[] at, ulong[] tint, float duration, float fade) {
            Plain = plain;
            this.start = start;
            this.at = at;
            this.tint = tint;
            Duration = duration;
            FadeDuration = fade;
            float shutter = TypeGlyphMotion.Shutter(fade);
            inverseMove = fade > 0.0f ? 1.0f / (fade - shutter) : 0.0f;
            exposure = shutter * inverseMove;
        }

        public string Plain { get; }

        public int Count => at.Length;

        public float Duration { get; }

        public float FadeDuration { get; }

        public bool HasColors => tint.Length != 0;

        public int Start(int element) => start[element];

        public int End(int element) => element + 1 < start.Length ? start[element + 1] : Plain.Length;

        public float At(int element) => at[element];

        public ulong Tint(int element) => tint.Length == 0 ? 0UL : tint[element];

        public bool Hard(int element) => Plain[start[element]] == '\n' || Plain[start[element]] == '\r';

        public int Revealed(float t) {
            if (!(t >= 0.0f)) { return 0; }
            if (t >= Duration) { return at.Length; }
            int lo = 0;
            int hi = at.Length;
            while (lo < hi) {
                int mid = (lo + hi) >> 1;
                if (at[mid] <= t) { lo = mid + 1; } else { hi = mid; }
            }
            return lo;
        }

        public float Alpha(int element, float t) {
            if (!(t >= at[element])) { return 0.0f; }
            if (t >= at[element] + FadeDuration) { return 1.0f; }
            if (inverseMove == 0.0f) { return 1.0f; }
            return TypeGlyphMotion.CoverageAt((t - at[element]) * inverseMove, exposure);
        }

        public TypeGlyphMotion Motion(int element, float t, float lineHeight) =>
            new TypeGlyphMotion((t - at[element]) * inverseMove, exposure, lineHeight * 0.07f, lineHeight * 0.24f);

        public static TypeText Parse(string source, in TypePace pace, IReadOnlyList<TypeRun> runs = null) {
            source ??= "";
            if (source.Length > MaxLength) { throw new ArgumentOutOfRangeException(nameof(source)); }
            string plain = source;
            IReadOnlyList<TypeRun> colors = runs;
            int[] bounds = StringInfo.ParseCombiningCharacters(plain);
            int n = bounds.Length;
            var at = new float[n];
            ulong[] tint = colors == null || colors.Count == 0 ? Array.Empty<ulong>() : new ulong[n];
            float time = 0.0f;
            float pauses = 0.0f;
            int visible = 0;
            int run = 0;
            int dots = 0;
            float pendingPause = 0.0f;
            float speed = pace.Clamped;
            float unit = 1.0f / (TypePace.BaseCps * speed);
            int printed = 0;
            for (int e = 0; e < n; e++) {
                at[e] = time;
                int s = bounds[e];
                int end = e + 1 < n ? bounds[e + 1] : plain.Length;
                char c = plain[s];
                if (!char.IsWhiteSpace(c)) { printed++; }
                if (c != '\n' && c != '\r') {
                    if (colors != null) {
                        while (run < colors.Count && colors[run].To < visible) { run++; }
                        if (run < colors.Count && colors[run].From <= visible && visible <= colors[run].To) { tint[e] = colors[run].Flags | colors[run].Color; }
                    }
                    visible += end - s;
                }
                if (!pace.Instant) {
                    time += Delay(plain, s, end, unit, speed, ref dots, ref pendingPause, out float pause);
                    if (e + 1 < n) { pauses += pause; }
                }
            }
            float fade = pace.Instant || n == 0 ? 0.0f : TypePace.FadeSeconds / speed;
            float travel = n == 0 ? 0.0f : at[n - 1];
            float total = n == 0 || pace.Instant ? 0.0f : travel + fade;
            if (total > 0.0f) {
                float minimum = (TypePace.ShortSeconds + TypePace.ShortIncrement * Math.Min(Math.Max(0, printed - 1), TypePace.ShortElements)) / speed;
                float motion = MathF.Max(total - pauses, minimum);
                fade = MathF.Min(motion, MathF.Max(fade, 2.0f * motion / (n + 1)));
                total = motion + pauses;
            }
            float from = TypePace.CompressFrom / speed;
            float max = TypePace.MaxSeconds / speed;
            if (total > from) {
                total = from + (max - from) * (1.0f - MathF.Exp(-(total - from) / max));
            }
            if (total > 0.0f) {
                float targetTravel = total - fade;
                if (targetTravel > travel && n > 1) {
                    float extra = (targetTravel - travel) / (n - 1);
                    for (int e = 1; e < n; e++) { at[e] += extra * e; }
                } else {
                    float scale = travel > 0.0f ? targetTravel / travel : 0.0f;
                    for (int e = 0; e < n; e++) { at[e] *= scale; }
                }
                total = at[n - 1] + fade;
            }
            return new TypeText(plain, bounds, at, tint, total, fade);
        }

        private static float Delay(string s, int from, int to, float unit, float speed, ref int dots, ref float pendingPause, out float pause) {
            char c = s[from];
            pause = 0.0f;
            if (char.IsWhiteSpace(c)) {
                dots = 0;
                pendingPause = 0.0f;
                return c == '\n' || c == '\r' ? TypePace.BreakPause / speed : unit * TypePace.SpaceShare;
            }
            float typed = unit;
            if (!Closing(c)) {
                dots = c == '.' ? dots + 1 : 0;
                if (c == '.') {
                    bool moreDots = to < s.Length && s[to] == '.';
                    if (dots > 1 || moreDots) { typed = MathF.Max(unit, TypePace.DotSeconds / speed); }
                    pendingPause = dots >= 3 ? TypePace.EllipsisPause : dots == 2 ? TypePace.TwoDotPause :
                        Digit(s, from - 1) && Digit(s, to) ? 0.0f : TypePace.StopPause;
                } else {
                    pendingPause = c == '…' ? TypePace.EllipsisPause : c == '!' || c == '?' ? TypePace.StopPause :
                        c == ',' || c == ';' || c == ':' || (c == '—' || c == '–') && from > 0 && s[from - 1] == ' ' ? TypePace.CommaPause : 0.0f;
                }
            }
            pause = to >= s.Length || char.IsWhiteSpace(s[to]) ? MathF.Min(pendingPause, TypePace.PauseCap) / speed : 0.0f;
            return typed + pause;
        }

        private static bool Closing(char c) => c == '"' || c == '»' || c == '”' || c == ')' || c == ']' || c == '\'' || c == '’';

        private static bool Digit(string s, int i) => i >= 0 && i < s.Length && char.IsDigit(s[i]);
    }

    public readonly struct TypeGlyph {
        public readonly int Codepoint;
        public readonly float Offset;

        public TypeGlyph(int codepoint, float offset) {
            Codepoint = codepoint;
            Offset = offset;
        }
    }

    public sealed class TypeLayout {
        private float[] x = Array.Empty<float>();
        private short[] line = Array.Empty<short>();
        private float[] advances = Array.Empty<float>();
        private int[] glyphFrom = Array.Empty<int>();
        private TypeGlyph[] glyphs = Array.Empty<TypeGlyph>();
        private float[] widths = new float[4];
        private int[] rowStarts = new int[4];

        public int Count { get; private set; }

        public int Lines { get; private set; }

        public float LineHeight { get; private set; }

        public float Width { get; private set; }

        public float Height => Lines * LineHeight;

        public bool Truncated { get; private set; }

        public float X(int element) => x[element];

        public int Line(int element) => line[element];

        public float LineWidth(int row) => widths[row];

        public ReadOnlySpan<TypeGlyph> Glyphs(int element) => glyphs.AsSpan(glyphFrom[element], glyphFrom[element + 1] - glyphFrom[element]);

        public float HeightFor(int revealed) => revealed <= 0 || Count == 0 ? 0.0f : (line[Math.Min(revealed, Count) - 1] + 1) * LineHeight;

        public float HeightAt(TypeText text, float t) {
            if (Count == 0) { return Truncated ? LineHeight : 0.0f; }
            float ahead = t + text.FadeDuration;
            int settled = ReachedRows(text, ahead - text.FadeDuration);
            int reached = ReachedRows(text, ahead);
            float height = settled + 1;
            
            
            
            for (int row = settled + 1; row <= reached; row++) { height += text.Alpha(rowStarts[row], ahead); }
            return MathF.Min(Lines, height) * LineHeight;
        }

        private int ReachedRows(TypeText text, float age) {
            if (!(age >= 0)) { return 0; }
            int lo = 1, hi = Lines;
            while (lo < hi) {
                int mid = (lo + hi) >> 1;
                if (text.At(rowStarts[mid]) <= age) { lo = mid + 1; } else { hi = mid; }
            }
            return lo - 1;
        }

        
        
        internal float ReserveSuffix(float advance, float maxWidth) {
            int row = Lines - 1;
            while (Count > 0 && line[Count - 1] == row && x[Count - 1] + advances[Count - 1] + advance > maxWidth) { Count--; }
            float suffixX = Count > 0 && line[Count - 1] == row ? x[Count - 1] + advances[Count - 1] : 0;
            widths[row] = suffixX + advance;
            Width = 0;
            for (int i = 0; i < Lines; i++) { Width = MathF.Max(Width, widths[i]); }
            return suffixX;
        }

        public void Wrap(TypeText text, IFace face, float maxWidth, float lineHeight, int maxLines) {
            if (!(maxWidth > 0) || !float.IsFinite(maxWidth) || maxLines < 1) { throw new ArgumentOutOfRangeException(nameof(maxWidth)); }
            Begin(text, face, lineHeight);
            int n = text.Count;
            float pen = 0.0f;
            int row = 0;
            int rowStart = 0;
            int lastBreak = -1;
            for (int e = 0; e < n; e++) {
                line[e] = (short)row;
                x[e] = pen;
                if (text.Hard(e)) {
                    widths[row] = pen;
                    row = Row(row);
                    pen = 0.0f;
                    rowStart = e + 1;
                    lastBreak = -1;
                    continue;
                }
                float advance = advances[e];
                bool space = text.Plain[text.Start(e)] == ' ';
                if (!space && pen + advance > maxWidth && e > rowStart) {
                    int from = lastBreak > rowStart ? lastBreak : e;
                    widths[row] = lastBreak > rowStart ? x[lastBreak - 1] : pen;
                    float shift = x[from];
                    row = Row(row);
                    for (int k = from; k <= e; k++) {
                        x[k] -= shift;
                        line[k] = (short)row;
                    }
                    pen = x[e];
                    rowStart = from;
                    lastBreak = -1;
                }
                pen += advance;
                if (space) { lastBreak = e + 1; }
            }
            widths[row] = pen;
            Finish(row, maxLines);
        }

        public void Follow(TypeText text, string wrapped, IFace face, float lineHeight) {
            if (wrapped == null) { throw new ArgumentNullException(nameof(wrapped)); }
            if (wrapped.Length > TypeText.MaxLength * 2) { throw new ArgumentOutOfRangeException(nameof(wrapped)); }
            Begin(text, face, lineHeight);
            int n = text.Count;
            string plain = text.Plain;
            int j = 0;
            float pen = 0.0f;
            int row = 0;
            for (int e = 0; e < n; e++) {
                int s = text.Start(e);
                char c = text.Hard(e) ? '\n' : plain[s];
                while (j < wrapped.Length && wrapped[j] == '\n' && c != '\n' && c != ' ') {
                    widths[row] = pen;
                    row = Row(row);
                    pen = 0.0f;
                    j++;
                }
                if (c == ' ' && j < wrapped.Length && wrapped[j] == '\n') {
                    x[e] = pen;
                    line[e] = (short)row;
                    widths[row] = pen;
                    row = Row(row);
                    pen = 0.0f;
                    j++;
                    continue;
                }
                x[e] = pen;
                line[e] = (short)row;
                if (c == '\n') {
                    widths[row] = pen;
                    row = Row(row);
                    pen = 0.0f;
                } else {
                    pen += advances[e];
                }
                j += text.End(e) - s;
            }
            widths[row] = pen;
            Finish(row, int.MaxValue);
        }

        private TypeText measuredText;
        private IFace measuredFace;

        private void Begin(TypeText text, IFace face, float lineHeight) {
            if (!(lineHeight > 0) || !float.IsFinite(lineHeight)) { throw new ArgumentOutOfRangeException(nameof(lineHeight)); }
            if (ReferenceEquals(measuredText, text) && ReferenceEquals(measuredFace, face)) {
                Count = text.Count; LineHeight = lineHeight; Truncated = false; return;
            }
            measuredText = null; measuredFace = null;
            int n = text.Count;
            if (x.Length < n + 1) {
                int size = Math.Max(n + 1, x.Length * 2);
                x = new float[size];
                line = new short[size];
                advances = new float[size];
                glyphFrom = new int[size + 1];
            }
            if (glyphs.Length < text.Plain.Length) { Array.Resize(ref glyphs, Math.Max(text.Plain.Length, glyphs.Length * 2)); }
            int count = 0;
            ReadOnlySpan<char> plain = text.Plain;
            for (int e = 0; e < n; e++) {
                glyphFrom[e] = count;
                float pen = 0.0f;
                ReadOnlySpan<char> span = plain.Slice(text.Start(e), text.End(e) - text.Start(e));
                if (!text.Hard(e)) {
                    for (int i = 0; i < span.Length; i++) {
                        int cp = span[i];
                        glyphs[count++] = new TypeGlyph(cp, pen);
                        pen += face.Advance(cp);
                    }
                }
                advances[e] = pen;
            }
            glyphFrom[n] = count;
            measuredText = text; measuredFace = face;
            Count = n;
            LineHeight = lineHeight;
            Truncated = false;
        }

        private void Finish(int row, int maxLines) {
            Lines = row + 1;
            if (Lines > maxLines) {
                Truncated = true;
                Lines = maxLines;
                for (int e = 0; e < Count; e++) {
                    if (line[e] >= maxLines) {
                        Count = e;
                        break;
                    }
                }
            }
            float w = 0.0f;
            for (int r = 0; r < Lines; r++) { w = MathF.Max(w, widths[r]); }
            Width = w;
            int nextRow = 0;
            for (int e = 0; e < Count; e++) {
                int rowIndex = line[e];
                while (nextRow <= rowIndex) { rowStarts[nextRow++] = e; }
            }
            
            while (nextRow < Lines) { rowStarts[nextRow++] = Math.Max(0, Count - 1); }
        }

        private int Row(int row) {
            if (row == short.MaxValue) { throw new ArgumentOutOfRangeException(nameof(row), "Too many wrapped lines"); }
            if (row + 2 > widths.Length) {
                Array.Resize(ref widths, widths.Length * 2);
                Array.Resize(ref rowStarts, widths.Length);
            }
            return row + 1;
        }

    }

}
