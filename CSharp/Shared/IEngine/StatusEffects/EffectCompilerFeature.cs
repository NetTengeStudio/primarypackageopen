using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using IEngine.Execution;

namespace IEngine.StatusEffects;

sealed class EffectCompilerFeature : Feature
{
    const int MaxPlans = 1 << 19;

    static EffectCompilerFeature instance;
    static readonly Dictionary<PropertyConditional, CompiledConditional> plans = new(1 << 14, ReferenceEqualityComparer.Instance);
    static MethodBase hasConditions, matches, matchesDirect;
    static string observers;
    static bool on, verify, bypass;
    static long evaluations, verified, mismatches;

    public override string Name => "effects";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        hasConditions = AccessTools.Method(typeof(StatusEffect), "HasRequiredConditions", new[] { typeof(IReadOnlyList<ISerializableEntity>), typeof(IReadOnlyList<PropertyConditional>), typeof(bool) });
        matches = AccessTools.Method(typeof(PropertyConditional), nameof(PropertyConditional.Matches));
        matchesDirect = AccessTools.Method(typeof(PropertyConditional), "MatchesDirect");
        if (hasConditions == null || matches == null || matchesDirect == null) { return "StatusEffect/PropertyConditional shape differs in this build"; }
        harmony.Patch(hasConditions, prefix: new HarmonyMethod(typeof(EffectCompilerFeature), nameof(HasConditionsPrefix)) { priority = Priority.First });
        harmony.Patch(matches, prefix: new HarmonyMethod(typeof(EffectCompilerFeature), nameof(MatchesPrefix)) { priority = Priority.First });
        var reset = new HarmonyMethod(typeof(EffectCompilerFeature), nameof(ResetPlans));
        foreach (MethodInfo m in typeof(GameSession).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (m.Name is nameof(GameSession.StartRound) or nameof(GameSession.EndRound)) { harmony.Patch(m, postfix: reset); }
        }
        return null;
    }

    internal override void OnLoadCompleted() => Refresh();
    protected override void OnToggled() => Refresh();
    internal override void RecheckObservers() => Refresh();

    static void Refresh()
    {
        var foreign = new[] { hasConditions, matches, matchesDirect }.Select(PatchCensus.Of).Where(f => f.Any).ToArray();
        observers = foreign.Length > 0 ? string.Join("; ", foreign) : null;
        on = instance.Active && observers == null;
        ResetPlans();
    }

    static void ResetPlans()
    {
        plans.Clear();
        CompiledConditional.Epoch++;
    }

    static CompiledConditional PlanOf(PropertyConditional c)
    {
        if (plans.TryGetValue(c, out CompiledConditional cc)) { return cc; }
        if (plans.Count >= MaxPlans) { plans.Clear(); }
        cc = new CompiledConditional(c);
        plans[c] = cc;
        return cc;
    }

    static bool MatchesPrefix(PropertyConditional __instance, ISerializableEntity target, ref bool __result)
    {
        if (!on || bypass || __instance.TargetContainedItem) { return true; }
        CompiledConditional cc = PlanOf(__instance);
        if (!cc.IsCompiled) { return true; }
        __result = verify ? Checked(cc, target) : cc.Match(target);
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

    static bool HasConditionsPrefix(StatusEffect __instance, IReadOnlyList<ISerializableEntity> targets, IReadOnlyList<PropertyConditional> conditionals, bool targetingContainer, ref bool __result)
    {
        if (!on || bypass) { return true; }
        var logic = __instance.conditionalLogicalOperator;
        if (logic is not (PropertyConditional.LogicalOperatorType.And or PropertyConditional.LogicalOperatorType.Or)) { return true; }
        evaluations++;
        __result = Evaluate(__instance, targets, conditionals, targetingContainer, logic == PropertyConditional.LogicalOperatorType.Or);
        if (verify)
        {
            bypass = true;
            try { if (__result != __instance.HasRequiredConditions(targets, conditionals, targetingContainer)) { mismatches++; } }
            finally { bypass = false; }
            verified++;
        }
        return false;
    }

    static bool Evaluate(StatusEffect effect, IReadOnlyList<ISerializableEntity> targets, IReadOnlyList<PropertyConditional> conditionals, bool targetingContainer, bool or)
    {
        int n = conditionals.Count;
        if (n == 0) { return true; }
        if (targets.Count == 0 && effect.requiredItems != null && effect.requiredItems.All(ri => ri.MatchOnEmpty)) { return true; }
        for (int i = 0; i < n; i++)
        {
            PropertyConditional pc = conditionals[i];
            bool r;
            if (!pc.TargetContainer | targetingContainer)
            {
                r = AnyTargetMatches(targets, pc.TargetItemComponent, pc);
            }
            else
            {
                Item item = OwnerItem(targets);
                if (item?.ParentInventory == null)
                {
                    r = pc.ComparisonOperator == PropertyConditional.ComparisonOperatorType.NotEquals;
                }
                else
                {
                    Entity entity = item.ParentInventory.Owner;
                    if (pc.TargetGrandParent && entity is Item grand) { entity = grand.ParentInventory?.Owner; }
                    if (entity is Item container)
                    {
                        r = pc.Type == PropertyConditional.ConditionType.HasTag ? Match(pc, container) : AnyTargetMatches(container.AllPropertyObjects, pc.TargetItemComponent, pc);
                    }
                    else if (entity is Character character)
                    {
                        r = Match(pc, character);
                    }
                    else { continue; }
                }
            }
            if (r == or) { return or; }
        }
        return !or;
    }

    static bool Match(PropertyConditional pc, ISerializableEntity target)
    {
        if (pc.TargetContainedItem) { return pc.Matches(target); }
        CompiledConditional cc = PlanOf(pc);
        return cc.IsCompiled ? cc.Match(target) : pc.Matches(target);
    }

    static bool AnyTargetMatches(IReadOnlyList<ISerializableEntity> list, string component, PropertyConditional pc)
    {
        bool any = string.IsNullOrEmpty(component);
        for (int i = 0; i < list.Count; i++)
        {
            ISerializableEntity t = list[i];
            if ((any || (t is ItemComponent ic && ic.Name == component)) && Match(pc, t)) { return true; }
        }
        return false;
    }

    static Item OwnerItem(IReadOnlyList<ISerializableEntity> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is Item item) { return item; }
            if (list[i] is ItemComponent ic) { return ic.Item; }
        }
        return null;
    }

    public override void SetVerify(bool value)
    {
        verify = value;
        verified = mismatches = 0;
    }

    public override string Report() =>
        $"compiling={on} plans={plans.Count} compiled={CompiledConditional.Compiled} fallback={CompiledConditional.Fallbacks} evaluations={evaluations}" +
        (observers != null ? $" disabled: observed by {observers}" : "") +
        (verify ? $" verified={verified} mismatches={mismatches}" : "");
}
