using System;
using PrimaryPackage.TextMotion;

namespace PrimaryPackage.TextMotion {
    public readonly struct TypeGlyphMotion {
        public const int Samples = 16;
        public const float ShutterSeconds = 0.045f;
        public const float MaxShutterShare = 0.3f;

        public readonly float Phase;
        public readonly float Exposure;
        public readonly float X;
        public readonly float Y;

        public TypeGlyphMotion(float phase, float exposure, float x, float y) {
            Phase = phase;
            Exposure = exposure;
            X = x;
            Y = y;
        }

        public float Coverage => CoverageAt(Phase, Exposure);

        public float SamplePhase(int sample) => Phase - Exposure + Exposure * ((sample + 0.5f) / Samples);

        public float OffsetAt(float phase) => 1.0f - TypeMotion.Ease(phase);

        public static float Shutter(float duration) => MathF.Min(ShutterSeconds, duration * MaxShutterShare);

        public static float CoverageAt(float phase, float exposure) {
            if (!(phase > 0.0f)) { return 0.0f; }
            if (phase >= 1.0f + exposure) { return 1.0f; }
            if (!(exposure > 0.0f) || !float.IsFinite(exposure)) { return TypeMotion.Ease(phase); }
            return (float)Math.Clamp((Integral(phase) - Integral((double)phase - exposure)) / exposure, 0.0, 1.0);
        }

        private static double Integral(double t) {
            if (t <= 0.0f) { return 0.0f; }
            if (t >= 1.0f) { return t - 0.5f; }
            double v = t <= 0.5f ? t : 1.0f - t;
            double value = v * v * v * v * (v * (v - 3.0f) + 2.5f);
            return t <= 0.5f ? value : t - 0.5f + value;
        }
    }
}
