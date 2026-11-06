using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using IEngine.Execution;

namespace IEngine.StatusEffects;

sealed class EffectCompilerFeature : Feature
{
    static EffectCompilerFeature instance;
    static readonly ConditionCompiler compiler = new();
    static MethodBase hasConditions, matches, matchesDirect, applyProperty, checkConditions;
    static MethodBase[] dependencies;
    static string observers, propertyObservers;
    static bool on, propertiesOn, verify, bypass;
    static long verified, mismatches, verifySkipped, propertyWrites;

    public override string Name => "effects";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        on = propertiesOn = false;
        if (!RuntimeFeature.IsDynamicCodeSupported) { return "Dynamic code generation is unavailable"; }
        hasConditions = AccessTools.Method(typeof(StatusEffect), "HasRequiredConditions", new[] { typeof(IReadOnlyList<ISerializableEntity>), typeof(IReadOnlyList<PropertyConditional>), typeof(bool) });
        matches = AccessTools.Method(typeof(PropertyConditional), nameof(PropertyConditional.Matches));
        matchesDirect = AccessTools.Method(typeof(PropertyConditional), "MatchesDirect");
        applyProperty = AccessTools.Method(typeof(StatusEffect), "ApplyToProperty");
        checkConditions = AccessTools.Method(typeof(PropertyConditional), nameof(PropertyConditional.CheckConditionals));
        if (hasConditions == null || matches == null || matchesDirect == null || applyProperty == null || checkConditions == null) { return "StatusEffect/PropertyConditional shape differs in this build"; }
        var observed = new List<MethodBase> { hasConditions, matches, matchesDirect };
        observed.AddRange(typeof(PropertyConditional).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        foreach (MethodInfo method in typeof(SerializableProperty).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.Name is "GetFloatValue" or "GetBoolValue" or "TryGetFloatValueWithoutReflection" or "TryGetBoolValueWithoutReflection" or
                "TrySetFloatValueWithoutReflection" or "TrySetBoolValueWithoutReflection" or "TrySetValue") { observed.Add(method); }
        }
        foreach (MethodInfo method in typeof(MathUtils).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name == nameof(MathUtils.NearlyEqual)) { observed.Add(method); }
        }
        dependencies = observed.Distinct().ToArray();
        harmony.Patch(hasConditions, prefix: new HarmonyMethod(typeof(EffectCompilerFeature), nameof(HasConditionsPrefix)) { priority = Priority.First });
        harmony.Patch(matches, prefix: new HarmonyMethod(typeof(EffectCompilerFeature), nameof(MatchesPrefix)) { priority = Priority.First });
        harmony.Patch(checkConditions, prefix: new HarmonyMethod(typeof(EffectCompilerFeature), nameof(CheckConditionsPrefix)) { priority = Priority.First });
        harmony.Patch(applyProperty, prefix: new HarmonyMethod(typeof(EffectCompilerFeature), nameof(PropertyPrefix)) { priority = Priority.First });
        var reset = new HarmonyMethod(typeof(EffectCompilerFeature), nameof(ResetPlans));
        foreach (MethodInfo m in typeof(GameSession).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (m.Name is nameof(GameSession.StartRound) or nameof(GameSession.EndRound)) { harmony.Patch(m, postfix: reset); }
        }
        harmony.Patch(AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.StopAll)), postfix: reset);
        return null;
    }

    internal override void OnLoadCompleted() => Refresh();
    protected override void OnToggled() => Refresh();
    internal override void RecheckObservers() => Refresh();
    internal override void Uninstall() { on = propertiesOn = false; ResetPlans(); }

    static void Refresh()
    {
        observers = null;
        foreach (MethodBase method in dependencies)
        {
            PatchCensus.Foreign foreign = PatchCensus.Of(method);
            if (foreign.Any) { observers = $"{method}: {foreign}"; break; }
        }
        on = instance.Active && observers == null;
        PatchCensus.Foreign property = PatchCensus.Of(applyProperty);
        propertyObservers = property.Any ? property.ToString() : observers;
        propertiesOn = instance.Active && propertyObservers == null;
        ResetPlans();
    }

    static void ResetPlans()
    {
        compiler.Clear();
    }

    static bool MatchesPrefix(PropertyConditional __instance, ISerializableEntity target, ref bool __result, bool __runOriginal)
    {
        if (!__runOriginal || !on || bypass || !CompiledConditional.Supports(__instance)) { return true; }
        CompiledConditional cc = compiler.Predicate(__instance);
        bool check = verify && cc.CanVerify(target);
        if (verify && !check) { verifySkipped++; }
        __result = check ? Checked(cc, target) : cc.Match(target);
        return false;
    }

    static bool CheckConditionsPrefix(ISerializableEntity conditionalTarget, IEnumerable<PropertyConditional> conditionals, PropertyConditional.LogicalOperatorType logicalOperator, ref bool __result, bool __runOriginal)
    {
        if (!__runOriginal || !on || bypass || logicalOperator is not (PropertyConditional.LogicalOperatorType.And or PropertyConditional.LogicalOperatorType.Or)) { return true; }
        if (!compiler.TryEvaluateDirect(conditionalTarget, conditionals, logicalOperator == PropertyConditional.LogicalOperatorType.Or, out __result)) { return true; }
        if (verify && compiler.LastEvaluationPure)
        {
            bypass = true;
            try { if (__result != PropertyConditional.CheckConditionals(conditionalTarget, conditionals, logicalOperator)) { mismatches++; } }
            finally { bypass = false; }
            verified++;
        }
        else if (verify) { verifySkipped++; }
        return false;
    }

    static bool Checked(CompiledConditional cc, ISerializableEntity target)
    {
        bool fast = cc.Match(target);
        bypass = true;
        try { if (fast != cc.Source.Matches(target)) { mismatches++; } }
        finally { bypass = false; }
        verified++;
        return fast;
    }

    static bool HasConditionsPrefix(StatusEffect __instance, IReadOnlyList<ISerializableEntity> targets, IReadOnlyList<PropertyConditional> conditionals, bool targetingContainer, ref bool __result, bool __runOriginal)
    {
        if (!__runOriginal || !on || bypass) { return true; }
        var logic = __instance.conditionalLogicalOperator;
        if (logic is not (PropertyConditional.LogicalOperatorType.And or PropertyConditional.LogicalOperatorType.Or)) { return true; }
        __result = compiler.Evaluate(__instance, targets, conditionals, targetingContainer, logic == PropertyConditional.LogicalOperatorType.Or);
        if (verify && compiler.LastEvaluationPure)
        {
            bypass = true;
            try { if (__result != __instance.HasRequiredConditions(targets, conditionals, targetingContainer)) { mismatches++; } }
            finally { bypass = false; }
            verified++;
        }
        else if (verify) { verifySkipped++; }
        return false;
    }

    static bool PropertyPrefix(StatusEffect __instance, ISerializableEntity target, SerializableProperty property, object value, float deltaTime, bool __runOriginal)
    {
        if (!__runOriginal || !propertiesOn || bypass || verify || property == null) { return true; }
        Type type = property.PropertyType;
        if (value is bool boolean && type == typeof(bool))
        {
            PropertyAccess.Of(property).WriteBool(target, boolean);
        }
        else if (value is int or float && (type == typeof(float) || type == typeof(int)))
        {
            PropertyAccess.Binding access = PropertyAccess.Of(property);
            float current = access.ReadFloat(target);
            float number = value is float single ? single : (int)value;
            float result = __instance.setValue ? number : current + number * (__instance.disableDeltaTime ? 1f : deltaTime);
            if (type == typeof(float)) { access.WriteFloat(target, result); }
            else { access.WriteInt(target, (int)result); }
        }
        else { return true; }
        propertyWrites++;
        return false;
    }

    public override void SetVerify(bool value)
    {
        verify = value;
        verified = mismatches = verifySkipped = 0;
    }

    public override string Report() =>
        $"compiling={on} propertyWrites={propertiesOn}/{propertyWrites} {compiler.Report()} accessors={PropertyAccess.Compiled} accessorFaults={PropertyAccess.Faults}" +
        (observers != null ? $" disabled: observed by {observers}" : "") +
        (propertyObservers != null ? $" property fallback: {propertyObservers}" : "") +
        (verify ? $" verified={verified} mismatches={mismatches} impureSkipped={verifySkipped}" : "");
}
