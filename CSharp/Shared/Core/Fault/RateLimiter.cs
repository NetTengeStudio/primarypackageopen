using System;

namespace PrimaryPackage.Core.Fault;

public sealed class RateLimiter {
    private readonly double capacity;
    private readonly double perSecond;
    private double tokens;
    private double stamp;
    private bool started;

    public int Suppressed { get; private set; }

    public RateLimiter(double capacity, double perSecond) {
        this.capacity = capacity;
        this.perSecond = perSecond;
        tokens = capacity;
    }

    public bool TryTake(double now, out int suppressedBefore) {
        if (!started) {
            started = true;
            stamp = now;
        }
        tokens = Math.Min(capacity, tokens + Math.Max(0.0, now - stamp) * perSecond);
        stamp = now;
        if (tokens >= 1.0) {
            tokens -= 1.0;
            suppressedBefore = Suppressed;
            Suppressed = 0;
            return true;
        }
        Suppressed++;
        suppressedBefore = 0;
        return false;
    }
}
