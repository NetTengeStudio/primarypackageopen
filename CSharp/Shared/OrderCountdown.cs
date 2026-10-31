using System;
namespace PrimaryPackage.Economy {
    public static class OrderCountdown {
        public static string Format(double seconds) {
            if (double.IsNaN(seconds) || seconds < 0) { seconds = 0; }
            int total = (int)Math.Round(seconds);
            int m = total / 60, s = total % 60;
            return $"{m:00}:{s:00}";
        }
    }
}
