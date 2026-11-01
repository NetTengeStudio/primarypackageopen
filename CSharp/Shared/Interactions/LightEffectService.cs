using System;
using System.Collections.Generic;
using System.Diagnostics;
using Barotrauma.Items.Components;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed class LightEffectService {
    private readonly TimedLeases<LightComponent> leases = new(128);
    public static LightEffectService Instance { get; } = new();
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public IEnumerable<(Guid Id, LightComponent Component, float Remaining)> Active {
        get {
            double now = Now;
            foreach (var effect in leases.Entries) { if (!effect.Target.Item.Removed && effect.End > now) { yield return (effect.Id, effect.Target, (float)(effect.End - now)); } }
        }
    }
    internal static void Attach(Harmony harmony) {
#if CLIENT
        harmony.Patch(AccessTools.Method(typeof(LightComponent), "SetLightSourceState"), prefix: ModuleHost.Bind(typeof(LightEffectService), nameof(Modulate)));
#endif
    }
#if CLIENT
    private const double PulseHertz = .8;
    private const float MinimumBrightness = .65f;
    private static void Modulate(LightComponent __instance, ref float brightness) {
        if (!Instance.leases.TryGetEnd(__instance, out double end) || end <= Now) { return; }
        // Smooth, low-contrast modulation; never activate vanilla's frame-random blackout/zap path.
        double wave = .5 - .5 * Math.Cos(Now * Math.Tau * PulseHertz);
        float envelope = (float)Math.Clamp((end - Now) / .35, 0, 1);
        brightness *= 1 - (1 - MinimumBrightness) * (float)wave * envelope;
    }
#endif
    public void Start(LightComponent component, float duration, Guid id = default) {
        if (component?.Item == null || component.Item.Removed || !float.IsFinite(duration) || duration <= 0 || duration > 10) { throw new ArgumentException("Invalid light effect"); }
        if (id == Guid.Empty) { id = Guid.NewGuid(); }
        leases.Start(id, component, Now + duration);
    }
    public void Cancel(Guid id) => leases.Cancel(id);
    public void Update() => leases.Expire(Now, static light => light.Item.Removed);
    public void Clear() => leases.Clear();
}
