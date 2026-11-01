using System;
using System.Numerics;

namespace PrimaryPackage.Interactions;

// Visual motion only. The simulation position advances continuously; the presentation can hold frames.
public sealed class PhantomMotion {
    private readonly Vector2 anchor;
    private readonly float radius, speed, nearDistance, cycleDistance;
    private readonly PhantomBehavior behavior;
    private double time, displayedAt;
    public Vector2 Position { get; private set; }
    public Vector2 Displayed { get; private set; }
    public Vector2 Ghost { get; private set; }
    public bool Vanished { get; private set; }
    public float Facing { get; private set; } = 1;
    public float DisplayedFacing { get; private set; } = 1;
    public float GhostFacing { get; private set; } = 1;
    public float Phase { get; private set; }
    public float DisplayedPhase { get; private set; }
    public float GhostPhase { get; private set; }
    public PhantomMotion(Vector2 anchor, float radius, float speed, PhantomBehavior behavior, double now, float nearDistance = 100, float cycleDistance = 100) {
        if (!float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y) || !float.IsFinite(radius) || radius < 10 || radius > 5000 ||
            !float.IsFinite(nearDistance) || nearDistance < 1 || nearDistance > 5000 || !float.IsFinite(cycleDistance) || cycleDistance <= 0 || !float.IsFinite(speed) || speed < 0 || speed > 500 || !Enum.IsDefined(behavior) || !double.IsFinite(now)) { throw new ArgumentException("Invalid phantom motion"); }
        this.anchor = anchor; this.radius = radius; this.speed = speed; this.nearDistance = Math.Min(radius, nearDistance); this.cycleDistance = cycleDistance; this.behavior = behavior;
        time = displayedAt = now; Position = Displayed = Ghost = anchor;
    }
    public void Advance(double now, Vector2 target, float intensity) {
        if (!double.IsFinite(now) || now < time || !float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(intensity) || intensity < 0 || intensity > 1) { throw new ArgumentException("Invalid phantom sample"); }
        double delta = now - time; time = now;
        Vector2 offset = target - Position;
        float distance = offset.Length();
        if (Math.Abs(offset.X) > .01f) { Facing = MathF.Sign(offset.X); }
        if (behavior == PhantomBehavior.VanishOnApproach && distance <= nearDistance) { Vanished = true; }
        Vector2 before = Position;
        if (distance > .001f && behavior is PhantomBehavior.Approach or PhantomBehavior.Pursue or PhantomBehavior.Retreat or PhantomBehavior.Lagged) {
            float travel = (float)Math.Min(speed * delta, behavior == PhantomBehavior.Retreat ? radius * 2 : Math.Max(0, distance - nearDistance));
            Position += offset * ((behavior == PhantomBehavior.Retreat ? -travel : travel) / distance);
            Vector2 fromAnchor = Position - anchor;
            float length = fromAnchor.Length();
            if (length > radius) { Position = anchor + fromAnchor * (radius / length); }
            if (behavior == PhantomBehavior.Approach && distance <= nearDistance) { Vanished = true; }
        }
        Phase += Vector2.Distance(before, Position) / cycleDistance * MathF.Tau;
        if (Phase > MathF.Tau) { Phase %= MathF.Tau; }
        double hold = behavior == PhantomBehavior.Lagged ? .05 + .15 * intensity : 0;
        if (now - displayedAt >= hold) { Ghost = Displayed; GhostPhase = DisplayedPhase; GhostFacing = DisplayedFacing; Displayed = Position; DisplayedPhase = Phase; DisplayedFacing = Facing; displayedAt = now; }
    }
}
