using System.Diagnostics;

namespace PrimaryPackage.TextMotion {
    public static class HudClock {
        private static readonly double Tick = 1.0 / Stopwatch.Frequency;

        public static double Now => Stopwatch.GetTimestamp() * Tick;
    }
}
