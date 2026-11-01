using System;

namespace PrimaryPackage.TextMotion;

public readonly struct Rgba {
    public readonly uint Packed;
    public Rgba(uint packed) => Packed = packed;
    public Rgba(int r, int g, int b, int a = 255) => Packed =
        (uint)(Math.Clamp(r, 0, 255) | Math.Clamp(g, 0, 255) << 8 |
        Math.Clamp(b, 0, 255) << 16 | Math.Clamp(a, 0, 255) << 24);
    public byte R => (byte)Packed;
    public byte G => (byte)(Packed >> 8);
    public byte B => (byte)(Packed >> 16);
    public byte A => (byte)(Packed >> 24);
    public Rgba Fade(float alpha) => new Rgba((Packed & 0x00FFFFFFu) |
        (uint)Math.Clamp((int)(A * alpha + 0.5f), 0, 255) << 24);
}

public readonly struct Box {
    public readonly float X, Y, W, H;
    public Box(float x, float y, float width, float height) {
        X = x; Y = y; W = MathF.Max(0, width); H = MathF.Max(0, height);
    }
    public float Right => X + W;
    public float Bottom => Y + H;
}

public interface IFace {
    float Advance(int codepoint);
}



public struct TextSpring {
    private double start, position, velocity, acceleration, target, frequency;
    private bool placed;
    public float Follow(float value, double now, double omega) {
        if (!float.IsFinite(value) || !double.IsFinite(now) || !(omega > 0) || !double.IsFinite(omega)) {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (!placed) {
            placed = true; start = now; position = target = value; velocity = acceleration = 0; frequency = omega;
            return value;
        }
        Sample(now, frequency, out double x, out double v, out double a);
        if (target != value || frequency != omega) { position = x; velocity = v; acceleration = a; target = value; start = now; frequency = omega; }
        return (float)x;
    }
    public void Snap(float value, double now) {
        this = default;
        Follow(value, now, 1);
    }
    private void Sample(double now, double omega, out double x, out double v, out double a) {
        double dt = Math.Max(0, now - start);
        double decay = Math.Exp(-omega * dt);
        if (decay == 0) { x = target; v = a = 0; return; }
        double displacement = position - target;
        double b = velocity + omega * displacement;
        double c = 0.5 * (acceleration + 2 * omega * velocity + omega * omega * displacement);
        double polynomial = displacement + dt * (b + c * dt);
        double derivative = b + 2 * c * dt;
        x = target + polynomial * decay;
        v = (derivative - omega * polynomial) * decay;
        a = (2 * c - 2 * omega * derivative + omega * omega * polynomial) * decay;
    }
}



public struct PopupLifetime {
    public TypePresentation Presentation;
    public bool Leaving { get; private set; }
    public double LeaveAt { get; private set; }
    private double last, reading;
    private bool observed;

    public bool Update(double now, double received, float duration, bool front, int count) {
        if (!Presentation.Started) { return now - received < TypeMotion.PendingSeconds; }
        if (Leaving) { return now - LeaveAt < TypeMotion.LeaveSeconds; }
        double end = now - Math.Max(0, Presentation.Age(now) - duration - TypeMotion.RevealLead);
        if (front) {
            double from = observed ? Math.Max(last, end) : Math.Max(received, end);
            reading += Math.Max(0, now - from) * Math.Max(count / 2, 1);
            if (reading >= 5) { Leaving = true; LeaveAt = now; }
        }
        observed = true; last = now;
        return true;
    }
    public float Opacity(double now) => TypeMotion.Ease(Presentation.Age(now) / TypeMotion.EnterSeconds) *
        (Leaving ? 1 - TypeMotion.Ease((float)(now - LeaveAt) / TypeMotion.LeaveSeconds) : 1);
}
