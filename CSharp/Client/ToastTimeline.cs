using System;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Zones {
    public struct ToastSpec {
        public string Text;
        public Color TextColor;
        public float Scale;
        public string FontName;
        public float MaxWidthPercent, VerticalOffset;
        public ZoneEasing Easing;
        public float StartDelay;
        public float FadeIn, Hold, FadeOut;
        public SlideFrom Slide;
        public float SlideOffset;
        public bool ScaleIn;
        public bool Typewriter;
        public float CharInterval, TypeVolume;
        public string TypeSound;
        public bool Shadow;
        public Vector2 ShadowOffset;
        public float ShadowBlur, ShadowAlpha;
        public Color ShadowColor;
        public bool Outline;
        public Color OutlineColor;
        public bool Background;
        public Color BackgroundColor;
        public float BackgroundAlpha, BackgroundPadding;

        public float Delay => Math.Max(StartDelay, 0.0f);

        public float InDuration(int charCount) {
            float typeDur = Typewriter ? charCount * Math.Max(CharInterval, 0.001f) : 0.0f;
            return Delay + Math.Max(FadeIn, typeDur);
        }

        public float Total(int charCount) => InDuration(charCount) + Hold + FadeOut;

        public bool VisualsEqual(in ToastSpec o) =>
            Text == o.Text && TextColor == o.TextColor && Scale == o.Scale && FontName == o.FontName &&
            MaxWidthPercent == o.MaxWidthPercent && VerticalOffset == o.VerticalOffset &&
            Easing == o.Easing && StartDelay == o.StartDelay &&
            FadeIn == o.FadeIn && Hold == o.Hold && FadeOut == o.FadeOut &&
            Slide == o.Slide && SlideOffset == o.SlideOffset && ScaleIn == o.ScaleIn &&
            Typewriter == o.Typewriter && CharInterval == o.CharInterval && TypeVolume == o.TypeVolume &&
            TypeSound == o.TypeSound &&
            Shadow == o.Shadow && ShadowOffset == o.ShadowOffset && ShadowBlur == o.ShadowBlur &&
            ShadowAlpha == o.ShadowAlpha && ShadowColor == o.ShadowColor &&
            Outline == o.Outline && OutlineColor == o.OutlineColor &&
            Background == o.Background && BackgroundColor == o.BackgroundColor &&
            BackgroundAlpha == o.BackgroundAlpha && BackgroundPadding == o.BackgroundPadding;
    }

    public struct ToastState {
        public float Alpha;
        public Vector2 Offset;
        public float Scale;
        public int VisibleChars;
        public bool Finished;
    }

    public static class ToastTimeline {
        public static float Ease(ZoneEasing e, float t) {
            t = MathHelper.Clamp(t, 0.0f, 1.0f);
            switch (e) {
                case ZoneEasing.Разгон: return t * t * t;
                case ZoneEasing.Торможение: { float u = 1.0f - t; return 1.0f - u * u * u; }
                case ZoneEasing.Плавно: return t * t * (3.0f - 2.0f * t);
                case ZoneEasing.Отскок: { float u = t - 1.0f; return 1.0f + u * u * (2.70158f * u + 1.70158f); }
                case ZoneEasing.Упруго:
                    if (t <= 0.0f) { return 0.0f; }
                    if (t >= 1.0f) { return 1.0f; }
                    return (float)(Math.Pow(2.0, -10.0 * t) * Math.Sin((t * 10.0 - 0.75) * (2.0 * Math.PI / 3.0))) + 1.0f;
                default: return t;
            }
        }

        public static ToastState Evaluate(in ToastSpec s, float elapsed, int charCount) {
            float total = s.Total(charCount);
            var st = new ToastState { Alpha = 1.0f, Offset = Vector2.Zero, Scale = 1.0f, VisibleChars = charCount };
            if (elapsed < 0.0f || elapsed >= total) {
                st.Finished = true;
                st.Alpha = 0.0f;
                st.VisibleChars = 0;
                return st;
            }
            float delay = s.Delay;
            float span = total - delay;
            elapsed -= delay;
            if (elapsed < 0.0f) {
                st.Alpha = 0.0f;
                st.VisibleChars = 0;
                if (s.ScaleIn) { st.Scale = 0.8f; }
                return st;
            }
            float tIn = s.FadeIn > 0.0f ? MathHelper.Clamp(elapsed / s.FadeIn, 0.0f, 1.0f) : 1.0f;
            float eased = Ease(s.Easing, tIn);
            st.Alpha = MathHelper.Clamp(eased, 0.0f, 1.0f);
            if (s.FadeOut > 0.0f && elapsed > span - s.FadeOut) {
                st.Alpha = MathHelper.Clamp(Ease(ZoneEasing.Плавно, (span - elapsed) / s.FadeOut), 0.0f, 1.0f);
            }
            if (s.Slide != SlideFrom.Нет && s.SlideOffset != 0.0f) {
                float k = 1.0f - eased;
                st.Offset = s.Slide switch {
                    SlideFrom.Снизу => new Vector2(0.0f, s.SlideOffset * k),
                    SlideFrom.Сверху => new Vector2(0.0f, -s.SlideOffset * k),
                    SlideFrom.Слева => new Vector2(-s.SlideOffset * k, 0.0f),
                    _ => new Vector2(s.SlideOffset * k, 0.0f)
                };
            }
            if (s.ScaleIn) { st.Scale = 0.8f + 0.2f * eased; }
            if (s.Typewriter) {
                st.VisibleChars = Math.Min(charCount, (int)(elapsed / Math.Max(s.CharInterval, 0.001f)) + 1);
            }
            return st;
        }
    }
}
