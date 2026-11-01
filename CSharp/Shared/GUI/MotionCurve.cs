using System;

namespace PrimaryPackage.GUI;

public readonly struct MotionCurve {
    private readonly double x1, y1, x2, y2;
    private readonly byte kind;
    private MotionCurve(byte kind) { this.kind = kind; x1 = y1 = x2 = y2 = 0; }
    public MotionCurve(double x1, double y1, double x2, double y2) {
        if (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2) ||
            x1 < 0 || x1 > 1 || x2 < 0 || x2 > 1 || y1 < 0 || y2 < y1 || y2 > 1) { throw new ArgumentException("A speed curve must be finite and monotone"); }
        this.x1 = x1; this.y1 = y1; this.x2 = x2; this.y2 = y2; kind = 2;
    }
    public static MotionCurve Linear => default;
    public static MotionCurve Smooth => new(1);
    public static MotionCurve EaseOut => new(.16, 1, .3, 1);
    public void Sample(double progress, out double position, out double speed) {
        if (!double.IsFinite(progress)) { throw new ArgumentOutOfRangeException(nameof(progress)); }
        double t = Math.Clamp(progress, 0, 1);
        if (kind == 0) { position = t; speed = progress is >= 0 and <= 1 ? 1 : 0; return; }
        if (kind == 1) {
            position = t * t * t * (10 + t * (-15 + 6 * t));
            speed = 30 * t * t * (1 - t) * (1 - t); return;
        }
        // Bracketed Newton iteration keeps flat endpoint tangents well conditioned.
        double lower = 0, upper = 1, u = t;
        for (int i = 0; i < 24; i++) {
            double x = Bezier(u, x1, x2), error = x - t;
            if (Math.Abs(error) <= 1e-12) { break; }
            if (error > 0) { upper = u; } else { lower = u; }
            double derivative = Derivative(u, x1, x2);
            double candidate = derivative > 1e-12 ? u - error / derivative : double.NaN;
            u = candidate > lower && candidate < upper ? candidate : (lower + upper) * .5;
        }
        position = Bezier(u, y1, y2);
        double dx = Derivative(u, x1, x2), dy = Derivative(u, y1, y2);
        speed = dx > 1e-12 ? dy / dx : 0;
        if (progress < 0 || progress > 1) { speed = 0; }
    }
    private static double Bezier(double t, double a, double b) => t * (3 * a + t * (3 * b - 6 * a + t * (1 + 3 * a - 3 * b)));
    private static double Derivative(double t, double a, double b) => 3 * a + t * (6 * b - 12 * a + t * (3 + 9 * a - 9 * b));
}
