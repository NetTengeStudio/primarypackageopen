using System;

namespace PrimaryPackage.Core.Fault;

public enum BreakerState : byte {
    Closed,
    Open,
    Probing
}

public sealed class FaultBreaker {
    private const int MaxLevel = 16;

    private double windowStart;
    private int inWindow;
    private double openUntil;
    private int level;

    public BreakerState State { get; private set; }
    public long Faults { get; private set; }
    public long Skipped { get; private set; }
    public long Trips { get; private set; }
    public double LastFault { get; private set; }
    public double OpenUntil => openUntil;
    public int Level => level;

    public bool Enter(double now) {
        switch (State) {
            case BreakerState.Closed:
                return true;
            case BreakerState.Open:
                if (now < openUntil) {
                    Skipped++;
                    return false;
                }
                State = BreakerState.Probing;
                return true;
            default:
                State = BreakerState.Closed;
                level = 0;
                inWindow = 0;
                return true;
        }
    }

    public bool Fault(double now, FaultPolicy policy) {
        Faults++;
        LastFault = now;
        if (State == BreakerState.Open) { return false; }
        if (State == BreakerState.Probing) {
            Trip(now, policy);
            return true;
        }
        if (now - windowStart > policy.WindowSeconds) {
            windowStart = now;
            inWindow = 0;
        }
        inWindow++;
        if (policy.MaySkip && inWindow >= policy.Threshold) {
            Trip(now, policy);
            return true;
        }
        return false;
    }

    public double Cooldown(FaultPolicy policy) {
        double cooldown = policy.CooldownSeconds * Math.Pow(2.0, Math.Min(level, MaxLevel));
        return Math.Min(cooldown, policy.MaxCooldownSeconds);
    }

    private void Trip(double now, FaultPolicy policy) {
        double cooldown = Cooldown(policy);
        State = BreakerState.Open;
        openUntil = now + cooldown;
        if (level < MaxLevel) { level++; }
        Trips++;
        inWindow = 0;
    }
}
