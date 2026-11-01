using Barotrauma;

namespace PrimaryPackage.Inspect {
    public struct InspectAnim {
        private const double Unit = 1.0 / uint.MaxValue;

        private uint prev, cur;

        public float Value => (float)(cur * Unit);
        public float Interpolated => (float)((prev + (cur - (double)prev) * Timing.Alpha) * Unit);
        public bool Idle => cur == 0u;
        public bool Full => cur == uint.MaxValue;

        public void Reset() => prev = cur = 0u;

        public bool Advance(bool up, float step, float duration) {
            prev = cur;
            if (duration <= 0.0f) {
                cur = up ? uint.MaxValue : 0u;
            } else {
                long units = (long)((double)step / duration * uint.MaxValue);
                if (units == 0L && step > 0.0f) { units = 1L; }
                long next = cur + (up ? units : -units);
                cur = next <= 0L ? 0u : next >= uint.MaxValue ? uint.MaxValue : (uint)next;
            }
            return up ? Full : Idle;
        }
    }

    public static class InspectClock {
        private const float MaxStep = 0.5f;

        private static double last;

        public static float Step { get; private set; }

        public static void Tick() {
            double now = Timing.TotalTime + Timing.Alpha * Timing.Step;
            float delta = (float)(now - last);
            last = now;
            Step = delta > 0.0f && delta < MaxStep ? delta : 0.0f;
        }
    }
}
