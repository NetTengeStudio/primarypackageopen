namespace PrimaryPackage.Core.Fault;

public sealed class FaultPolicy {
    public readonly int Threshold;
    public readonly double WindowSeconds;
    public readonly double CooldownSeconds;
    public readonly double MaxCooldownSeconds;
    public readonly bool MaySkip;

    public FaultPolicy(int threshold, double windowSeconds, double cooldownSeconds, double maxCooldownSeconds, bool maySkip) {
        Threshold = threshold;
        WindowSeconds = windowSeconds;
        CooldownSeconds = cooldownSeconds;
        MaxCooldownSeconds = maxCooldownSeconds;
        MaySkip = maySkip;
    }

    public static readonly FaultPolicy Tick = new FaultPolicy(5, 10.0, 5.0, 60.0, true);

    public static readonly FaultPolicy Essential = new FaultPolicy(int.MaxValue, 10.0, 0.0, 0.0, false);

    public static readonly FaultPolicy Entity = new FaultPolicy(3, 5.0, 30.0, 300.0, true);
}
