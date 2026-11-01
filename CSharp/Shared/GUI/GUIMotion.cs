using System;
using System.Collections.Generic;

namespace PrimaryPackage.GUI;

public readonly struct MotionSettings {
    public readonly double Frequency;
    public readonly double Damping;
    public readonly double Precision;

    public MotionSettings(double settlingTime, double damping = 1, double precision = 0.0001) {
        if (!double.IsFinite(settlingTime) || settlingTime < 0.001 || settlingTime > 60 ||
            !double.IsFinite(damping) || damping < 0.05 || damping > 10 ||
            !double.IsFinite(precision) || precision <= 0 || precision > 0.1) {
            throw new ArgumentOutOfRangeException(nameof(settlingTime));
        }
        Damping = damping;
        Precision = precision;
        Frequency = 9.233413476451586 / settlingTime;
    }

    public static MotionSettings Open => new(0.22);
    public static MotionSettings Hover => new(0.12);
    public static MotionSettings Scroll => new(0.18);
}

public sealed class MotionValue : IDisposable {
    internal readonly GUIMotion Owner;
    internal MotionSettings Settings;
    internal double Origin, InitialVelocity, Started;
    internal int ActiveIndex = -1;
    internal bool Disposed;
    public double Position { get; internal set; }
    public double Velocity { get; internal set; }
    public double Target { get; internal set; }
    public bool Moving => ActiveIndex >= 0;
    public event Action<MotionValue> Changed;
    internal bool Observed => Changed != null;

    internal MotionValue(GUIMotion owner, double value, MotionSettings settings) {
        Owner = owner;
        Settings = settings;
        Origin = Position = Target = value;
    }

    public void MoveTo(double target) => Owner.Move(this, target);
    public void Finish() => Owner.Finish(this);
    public void Dispose() {
        if (Disposed) { return; }
        Disposed = true;
        Owner.Detach(this);
        Changed = null;
    }

    internal void Notify() => Changed?.Invoke(this);
}

public sealed class GUIMotion {
    private readonly List<MotionValue> active = new();
    private readonly List<MotionValue> changed = new();
    private bool advancing;
    private double time;
    public static GUIMotion Shared { get; } = new();
    public int ActiveCount => active.Count;
    public double Time => time;

    public MotionValue Create(double value, MotionSettings settings) {
        if (!double.IsFinite(value) || Math.Abs(value) > 1e12 || !(settings.Frequency > 0)) { throw new ArgumentOutOfRangeException(nameof(value)); }
        return new MotionValue(this, value, settings);
    }

    public void Advance(double now) {
        if (!double.IsFinite(now)) { throw new ArgumentOutOfRangeException(nameof(now)); }
        if (advancing) { throw new InvalidOperationException("Motion scheduler cannot advance reentrantly"); }
        time = Math.Max(time, now);
        advancing = true;
        try {
            for (int i = active.Count - 1; i >= 0; i--) {
                MotionValue value = active[i];
                Evaluate(value, time);
                if (AtRest(value)) { Settle(value); }
                if (value.Observed) { changed.Add(value); }
            }
            for (int i = 0; i < changed.Count; i++) { if (!changed[i].Disposed) { changed[i].Notify(); } }
        } finally {
            changed.Clear();
            advancing = false;
        }
    }

    public void FinishAll() {
        while (active.Count > 0) { Finish(active[active.Count - 1]); }
    }

    internal void Move(MotionValue value, double target) {
        if (value.Disposed) { throw new ObjectDisposedException(nameof(MotionValue)); }
        if (!double.IsFinite(target) || Math.Abs(target) > 1e12) { throw new ArgumentOutOfRangeException(nameof(target)); }
        if (value.Target == target) { return; }
        if (value.Moving) { Evaluate(value, time); }
        value.Origin = value.Position - target;
        value.InitialVelocity = value.Velocity;
        value.Started = time;
        value.Target = target;
        if (!value.Moving) {
            value.ActiveIndex = active.Count;
            active.Add(value);
        }
    }

    internal void Finish(MotionValue value) {
        Settle(value);
        value.Notify();
    }
    internal void Detach(MotionValue value) => Settle(value);

    private void Settle(MotionValue value) {
        if (value.Moving) {
            int index = value.ActiveIndex;
            MotionValue last = active[active.Count - 1];
            active[index] = last;
            last.ActiveIndex = index;
            active.RemoveAt(active.Count - 1);
            value.ActiveIndex = -1;
        }
        value.Position = value.Target;
        value.Velocity = 0;
        value.Origin = value.InitialVelocity = 0;
    }

    private static bool AtRest(MotionValue value) {
        double displacement = value.Position - value.Target;
        double speed = value.Velocity / value.Settings.Frequency;
        return displacement * displacement + speed * speed <= value.Settings.Precision * value.Settings.Precision;
    }

    private static void Evaluate(MotionValue value, double now) {
        double elapsed = Math.Max(0, now - value.Started);
        double omega = value.Settings.Frequency, damping = value.Settings.Damping;
        double x = value.Origin, v = value.InitialVelocity;
        double displacement, velocity;
        if (damping == 1) {
            double z = omega * elapsed;
            if (z > 745) { value.Position = value.Target; value.Velocity = 0; return; }
            double term = v + omega * x, decay = Math.Exp(-z);
            displacement = (x + term * elapsed) * decay;
            velocity = (v - omega * term * elapsed) * decay;
        } else if (damping < 1) {
            double decayRate = omega * damping;
            double frequency = omega * Math.Sqrt(1 - damping * damping);
            double decay = Math.Exp(-decayRate * elapsed);
            if (decay == 0) { value.Position = value.Target; value.Velocity = 0; return; }
            double c = Math.Cos(frequency * elapsed), s = Math.Sin(frequency * elapsed);
            double b = (v + decayRate * x) / frequency;
            displacement = decay * (x * c + b * s);
            velocity = decay * ((b * frequency - decayRate * x) * c - (x * frequency + decayRate * b) * s);
        } else {
            double root = Math.Sqrt(damping * damping - 1);
            double r1 = -omega / (damping + root), r2 = -omega * (damping + root);
            double e1 = Math.Exp(r1 * elapsed), e2 = Math.Exp(r2 * elapsed);
            double frequency = omega * root, z = frequency * elapsed;
            double average = (e1 + e2) * .5;
            double integral;
            if (z < .001) {
                double squared = z * z;
                integral = Math.Exp(-omega * damping * elapsed) * elapsed *
                    (1 + squared * (1d / 6 + squared * (1d / 120 + squared / 5040)));
            } else { integral = (e1 - e2) / (2 * frequency); }
            displacement = x * average + (v + omega * damping * x) * integral;
            velocity = v * average - (omega * damping * v + omega * omega * x) * integral;
        }
        value.Position = value.Target + displacement;
        value.Velocity = velocity;
    }
}
