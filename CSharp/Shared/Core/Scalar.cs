using System;

namespace PrimaryPackage.Core {
    public static class Scalar {
        private const int Steps = 256;
        private const float StepsF = Steps;
        private const int NoiseMask = 63;
        private const float DecayRange = 6.0f;

        private static readonly float[] smooth = new float[Steps + 2];
        private static readonly float[] outCubic = new float[Steps + 2];
        private static readonly float[] inCubic = new float[Steps + 2];
        private static readonly float[] outBack = new float[Steps + 2];
        private static readonly float[] decay = new float[Steps + 2];
        private static readonly float[] sine = new float[Steps + 2];
        private static readonly float[] noise = new float[NoiseMask + 2];

        static Scalar() {
            for (int i = 0; i <= Steps + 1; i++) {
                double t = i / (double)Steps;
                if (t > 1.0) { t = 1.0; }
                smooth[i] = (float)(t * t * (3.0 - 2.0 * t));
                double u = 1.0 - t;
                outCubic[i] = (float)(1.0 - u * u * u);
                inCubic[i] = (float)(t * t * t);
                double p = t - 1.0;
                outBack[i] = (float)(1.0 + 2.70158 * p * p * p + 1.70158 * p * p);
                decay[i] = (float)(1.0 - Math.Exp(-t * DecayRange));
                sine[i] = (float)Math.Sin(t * Math.PI * 2.0);
            }
            uint state = 0x9E3779B9u;
            for (int i = 0; i <= NoiseMask; i++) {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                noise[i] = (state >> 8) * (2.0f / 16777216.0f) - 1.0f;
            }
            noise[NoiseMask + 1] = noise[0];
        }

        public static float Smooth(float t) => Sample(smooth, t);

        public static float OutCubic(float t) => Sample(outCubic, t);

        public static float InCubic(float t) => Sample(inCubic, t);

        public static float OutBack(float t) => Sample(outBack, t);

        public static float Decay(float rate, float dt) {
            float x = rate * dt;
            if (x <= 0.0f) { return 0.0f; }
            if (x >= DecayRange) { return 1.0f; }
            return Sample(decay, x * (1.0f / DecayRange));
        }

        public static float Sine(float phase) {
            phase -= (float)Math.Floor(phase);
            int index = (int)(phase * StepsF);
            float frac = phase * StepsF - index;
            return sine[index] + (sine[index + 1] - sine[index]) * frac;
        }

        public static float Noise(float phase) {
            float floor = (float)Math.Floor(phase);
            int index = (int)((long)floor & NoiseMask);
            float frac = phase - floor;
            float weight = frac * frac * (3.0f - 2.0f * frac);
            float a = noise[index];
            float b = noise[index + 1];
            return a + (b - a) * weight;
        }

        public static float Approach(float current, float target, float rate, float dt) =>
            current + (target - current) * Decay(rate, dt);

        public static float Clamp01(float value) => value < 0.0f ? 0.0f : value > 1.0f ? 1.0f : value;

        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float Sample(float[] table, float t) {
            if (t <= 0.0f) { return table[0]; }
            if (t >= 1.0f) { return table[Steps]; }
            float scaled = t * StepsF;
            int index = (int)scaled;
            float frac = scaled - index;
            return table[index] + (table[index + 1] - table[index]) * frac;
        }
    }
}
