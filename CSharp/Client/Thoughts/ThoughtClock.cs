using Barotrauma;

namespace PrimaryPackage.Thoughts {
    public static class ThoughtClock {
        private const float MaxStep = 0.25f;

        private static double last;

        public static float Step { get; private set; }

        public static float Time { get; private set; }

        public static void Tick() {
            double now = Timing.TotalTime + Timing.Alpha * Timing.Step;
            if (now == last) { return; }
            float delta = (float)(now - last);
            last = now;
            Step = delta > 0.0f && delta < MaxStep ? delta : 0.0f;
            Time += Step;
            if (Time > 100000.0f) { Time -= 100000.0f; }
        }
    }
}
