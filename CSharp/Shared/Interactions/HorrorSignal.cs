using System;

namespace PrimaryPackage.Interactions;

internal readonly record struct HorrorSignalFrame(float Darkness, float Displacement, uint Pattern);

internal static class HorrorSignal {
    // Quantize presentation only. Simulation, input and the render thread keep running.
    internal static HorrorSignalFrame Sample(double elapsed, float duration, float intensity, uint seed) {
        if (!double.IsFinite(elapsed) || !float.IsFinite(duration) || duration <= 0 || !float.IsFinite(intensity) || intensity < 0 || intensity > 1) { throw new ArgumentOutOfRangeException(nameof(elapsed)); }
        if (elapsed < 0 || elapsed >= duration) { return default; }
        float envelope = (float)Math.Clamp(Math.Min(elapsed / .25, (duration - elapsed) / .5), 0, 1) * intensity;
        uint frame = (uint)(elapsed * 8), pattern = Hash(frame ^ seed);
        // Two short dark intervals per 1.6 seconds, no high-frequency full-screen flicker.
        double phase = elapsed % 1.6;
        float pulse = phase is > .45 and < .53 or > 1.2 and < 1.28 ? 1 : .3f;
        return new(envelope * pulse * .85f, envelope * (((pattern & 255) / 255f) * 2 - 1), pattern);
    }
    private static uint Hash(uint value) {
        value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15; value *= 0x846ca68b; return value ^ value >> 16;
    }
}

internal static class HorrorPresets {
    internal static SceneProgram Create(int preset, string visual) => preset switch {
        0 => new SceneProgram { Name = "Black Glitch", Steps = new() {
            new() { Action = SceneAction.Hallucination, Visual = visual, Behavior = PhantomBehavior.Lagged, Duration = 5, Intensity = .75f, Speed = 45 },
            new() { Action = SceneAction.SignalCollapse, Duration = 5, Intensity = .55f }
        } },
        1 => new SceneProgram { Name = "Phantom Encounter", Steps = new() {
            new() { Action = SceneAction.Hallucination, Visual = visual, Behavior = PhantomBehavior.Approach, Duration = 8, Intensity = .6f, Speed = 65, NearDistance = 100 }
        } },
        2 => new SceneProgram { Name = "Signal Collapse", Steps = new() { new() { Action = SceneAction.SignalCollapse, Duration = 4, Intensity = .7f } } },
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };
}
