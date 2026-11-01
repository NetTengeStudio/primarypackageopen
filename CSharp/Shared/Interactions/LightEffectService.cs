using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using Barotrauma.Items.Components;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Interactions;

public sealed class LightEffectService {
    private readonly Dictionary<LightComponent, double> active = new();
    private readonly List<LightComponent> expired = new();
    public static LightEffectService Instance { get; } = new();
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public IEnumerable<(LightComponent Component, float Remaining)> Active {
        get {
            double now = Now;
            foreach (var effect in active) { if (!effect.Key.Item.Removed && effect.Value > now) { yield return (effect.Key, (float)(effect.Value - now)); } }
        }
    }
    internal static void Attach(Harmony harmony) => harmony.Patch(AccessTools.Method(typeof(LightComponent), nameof(LightComponent.Update)), transpiler: ModuleHost.Bind(typeof(LightEffectService), nameof(Transpile)));
    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> source) {
        var instructions = new List<CodeInstruction>(source);
        var timer = AccessTools.Field(typeof(LightComponent), nameof(LightComponent.TemporaryFlickerTimer));
        int replaced = 0;
        for (int i = 0; i + 1 < instructions.Count; i++) {
            if (!instructions[i].LoadsField(timer) || instructions[i + 1].opcode != OpCodes.Ldc_R4 || !Equals(instructions[i + 1].operand, 0f)) { continue; }
            instructions[i].opcode = OpCodes.Call;
            instructions[i].operand = AccessTools.Method(typeof(LightEffectService), nameof(EffectiveTimer));
            replaced++;
        }
        if (replaced != 1) { throw new InvalidOperationException("LightComponent flicker condition changed: " + replaced); }
        return instructions;
    }
    public static float EffectiveTimer(LightComponent component) => Instance.active.TryGetValue(component, out double until)
        ? Math.Max(component.TemporaryFlickerTimer, (float)Math.Max(0, until - Now)) : component.TemporaryFlickerTimer;
    public void Start(LightComponent component, float duration) {
        if (component?.Item == null || component.Item.Removed || !float.IsFinite(duration) || duration <= 0 || duration > 10) { throw new ArgumentException("Invalid light effect"); }
        if (active.Count >= 128 && !active.ContainsKey(component)) { throw new InvalidOperationException("Too many light effects"); }
        active[component] = Now + duration;
    }
    public void Update() {
        if (active.Count == 0) { return; }
        double now = Now;
        expired.Clear();
        foreach (var effect in active) { if (effect.Key.Item.Removed || effect.Value <= now) { expired.Add(effect.Key); } }
        for (int i = 0; i < expired.Count; i++) { active.Remove(expired[i]); }
        expired.Clear();
    }
    public void Clear() { active.Clear(); expired.Clear(); }
}
