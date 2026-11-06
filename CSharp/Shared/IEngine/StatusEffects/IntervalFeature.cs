using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Barotrauma;
using HarmonyLib;
using IEngine.Execution;
using Microsoft.Xna.Framework;

namespace IEngine.StatusEffects;

sealed class IntervalFeature : Feature
{
    static IntervalFeature instance;
    static MethodInfo wait;
    static MethodInfo[] callers;
    static MethodBase[] observed;
    static string observers;
    static readonly HashSet<MethodBase> rewritten = new();
    static volatile bool on;

    public override string Name => "intervals";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        on = false;
        wait = AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.ShouldWaitForInterval));
        if (wait?.GetMethodBody() == null) { return "StatusEffect.ShouldWaitForInterval is unavailable"; }
        try { IntervalContract.Validate(PatchProcessor.GetOriginalInstructions(wait)); }
        catch (InvalidOperationException exception) { return exception.Message; }
        MethodInfo removed = AccessTools.PropertyGetter(typeof(Entity), nameof(Entity.Removed));
        var methods = new List<MethodBase> { removed };
        int predicates = 0;
        foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(wait))
        {
            if (instruction.opcode == OpCodes.Ldftn && instruction.operand is MethodInfo predicate)
            {
                List<CodeInstruction> body = PatchProcessor.GetOriginalInstructions(predicate);
                if (body.Count != 3 || body[0].opcode != (predicate.IsStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1) ||
                    !body[1].Calls(removed) || body[2].opcode != OpCodes.Ret)
                {
                    return "Interval expiry predicate differs from the verified implementation";
                }
                methods.Add(predicate);
                predicates++;
            }
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(System.Linq.Enumerable)) { methods.Add(method); }
        }
        if (predicates != 1) { return "Interval expiry predicate is unavailable"; }
        observed = methods.ToArray();
        callers = new[]
        {
            AccessTools.Method(typeof(Item), nameof(Item.ApplyStatusEffect)),
            AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.Apply), new[] { typeof(ActionType), typeof(float), typeof(Entity), typeof(IReadOnlyList<ISerializableEntity>), typeof(Vector2?) }),
            AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.Apply), new[] { typeof(float), typeof(Entity), typeof(IReadOnlyList<ISerializableEntity>), typeof(Vector2?) })
        };
        foreach (MethodInfo caller in callers)
        {
            if (caller == null) { return "Interval timer caller is unavailable"; }
            int count = 0;
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(caller)) { if (instruction.Calls(wait)) { count++; } }
            if (count != 1) { return $"{caller} has {count} interval checks, expected 1"; }
        }
        rewritten.Clear();
        foreach (MethodInfo caller in callers)
        {
            harmony.Patch(caller, transpiler: new HarmonyMethod(typeof(IntervalFeature), nameof(Transpile)) { priority = Priority.Last });
        }
        return rewritten.Count == callers.Length ? null : $"Interval timer calls specialized {rewritten.Count}/{callers.Length}";
    }

    internal override void OnLoadCompleted() => Refresh();
    internal override void RecheckObservers() => Refresh();
    protected override void OnToggled() => Refresh();
    internal override void Uninstall() => on = false;

    static void Refresh()
    {
        observers = null;
        PatchCensus.Foreign body = PatchCensus.Of(wait);
        if (body.Any) { observers = $"{wait}: {body}"; }
        if (observers == null)
        {
            foreach (MethodInfo caller in callers)
            {
                PatchCensus.Foreign foreign = PatchCensus.Of(caller);
                if (foreign.Transpilers.Length != 0) { observers = $"{caller}: {foreign}"; break; }
            }
        }
        if (observers == null)
        {
            foreach (MethodBase method in observed)
            {
                PatchCensus.Foreign foreign = PatchCensus.Of(method);
                if (foreign.Any) { observers = $"{method.DeclaringType}.{method.Name}: {foreign}"; break; }
            }
        }
        on = instance.Active && rewritten.Count == callers.Length && observers == null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ShouldWait(StatusEffect effect, Entity entity, float deltaTime)
    {
        if (!on) { return Original(effect, entity, deltaTime); }
        if (!(effect.Interval > 0f) || entity == null || effect.intervalTimers == null) { return false; }
        return WaitActive(effect, entity, deltaTime);
    }

    static bool WaitActive(StatusEffect effect, Entity entity, float deltaTime) =>
        ReferenceEquals(effect.intervalTimers.Comparer, EqualityComparer<Entity>.Default)
            ? IntervalTimers.ShouldWait(effect, entity, deltaTime)
            : Original(effect, entity, deltaTime);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    static bool Original(StatusEffect effect, Entity entity, float deltaTime) => effect.ShouldWaitForInterval(entity, deltaTime);

    static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = new List<CodeInstruction>(instructions);
        rewritten.Remove(__originalMethod);
        int site = -1;
        for (int i = 0; i < code.Count; i++)
        {
            if (!code[i].Calls(wait)) { continue; }
            if (site >= 0) { return code; }
            site = i;
        }
        if (site < 0) { return code; }
        CodeInstruction call = code[site];
        code[site] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(IntervalFeature), nameof(ShouldWait)))
        {
            labels = call.labels,
            blocks = call.blocks
        };
        rewritten.Add(__originalMethod);
        return code;
    }

    public override string Report() => $"active={on} intervalChecks={rewritten.Count}" +
        (observers != null ? $" disabled: observed by {observers}" : "");
}
