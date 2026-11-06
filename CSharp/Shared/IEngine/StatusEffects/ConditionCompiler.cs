using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace IEngine.StatusEffects;

// One owner for scalar bindings, logical plans and their round lifetime.
// Opaque operations always cross the same evaluation path as the interpreter.
sealed class ConditionCompiler
{
    internal static readonly AccessTools.FieldRef<List<PropertyConditional>, int> Version = AccessTools.FieldRefAccess<List<PropertyConditional>, int>("_version");
    readonly Dictionary<PropertyConditional, CompiledConditional> predicates = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<List<PropertyConditional>, ConditionProgram> programs = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<List<PropertyConditional>, ConditionProgram> directPrograms = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<PropertyConditional[], List<PropertyConditional>> immutableSources = new(ReferenceEqualityComparer.Instance);
    readonly List<ISerializableEntity[]> directFrames = new();
    int directDepth;
    ConditionProgram last;
    public int Epoch { get; private set; }
    public bool LastEvaluationPure { get; private set; }
    public long Evaluations, KernelCalls, InterpreterCalls, Kernels, Folded, Duplicates, SharedReads, Deoptimizations;
    public IEngine.Core.CostMeter Compilation;
    public int PredicateCount => predicates.Count;
    public int ProgramCount => programs.Count + directPrograms.Count;

    public bool TryEvaluateDirect(ISerializableEntity target, IEnumerable<PropertyConditional> conditionals, bool or, out bool result)
    {
        result = true;
        LastEvaluationPure = false;
        List<PropertyConditional> source;
        if (conditionals == null) { LastEvaluationPure = true; return true; }
        if (conditionals.GetType() == typeof(List<PropertyConditional>)) { source = (List<PropertyConditional>)conditionals; }
        else if (conditionals is ImmutableArray<PropertyConditional> immutable && !immutable.IsDefault)
        {
            PropertyConditional[] array = ImmutableCollectionsMarshal.AsArray(immutable);
            if (!immutableSources.TryGetValue(array, out source)) { immutableSources.Add(array, source = new List<PropertyConditional>(array)); }
        }
        else { return false; }
        Evaluations++;
        if (source.Count == 0) { LastEvaluationPure = true; return true; }
        if (directDepth == directFrames.Count) { directFrames.Add(new ISerializableEntity[1]); }
        ISerializableEntity[] frame = directFrames[directDepth++];
        frame[0] = target;
        try
        {
            if (!directPrograms.TryGetValue(source, out ConditionProgram program)) { directPrograms.Add(source, program = new ConditionProgram(this, source, true)); }
            if (program.TryEvaluate(frame, false, or, out result, out bool pure)) { LastEvaluationPure = pure; KernelCalls++; }
            else { InterpreterCalls++; result = EvaluateDirectRange(target, source, or, 0, Version(source)); }
            return true;
        }
        finally { frame[0] = null; directDepth--; }
    }

    internal bool EvaluateDirectRange(ISerializableEntity target, List<PropertyConditional> source, bool or, int start, int version)
    {
        for (int i = start; ; i++)
        {
            if (Version(source) != version) { throw new InvalidOperationException("Collection was modified; enumeration operation may not execute."); }
            if (i >= source.Count) { return !or; }
            if (Match(source[i], target) == or) { return or; }
        }
    }

    public CompiledConditional Predicate(PropertyConditional source)
    {
        if (!predicates.TryGetValue(source, out CompiledConditional predicate))
        {
            predicate = new CompiledConditional(source);
            predicates.Add(source, predicate);
        }
        return predicate;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(StatusEffect effect, IReadOnlyList<ISerializableEntity> targets, IReadOnlyList<PropertyConditional> conditionals, bool targetingContainer, bool or)
    {
        Evaluations++;
        LastEvaluationPure = false;
        if (conditionals.Count == 0)
        {
            LastEvaluationPure = conditionals.GetType() == typeof(List<PropertyConditional>) || conditionals.GetType() == typeof(PropertyConditional[]);
            return true;
        }
        if (targets.Count == 0 && effect.requiredItems != null && MatchOnEmpty(effect.requiredItems)) { return true; }
        if (conditionals is List<PropertyConditional> source && targets != null)
        {
            ConditionProgram program = last;
            if (program == null || !ReferenceEquals(program.Source, source))
            {
                program = Resolve(source);
            }
            if (program.TryEvaluate(targets, targetingContainer, or, out bool result, out bool pure)) { LastEvaluationPure = pure; KernelCalls++; return result; }
        }
        InterpreterCalls++;
        LastEvaluationPure = false;
        return EvaluateRange(targets, conditionals, targetingContainer, or, 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    ConditionProgram Resolve(List<PropertyConditional> source)
    {
        if (!programs.TryGetValue(source, out ConditionProgram program))
        {
            program = new ConditionProgram(this, source);
            programs.Add(source, program);
        }
        last = program;
        return program;
    }

    static bool MatchOnEmpty(List<RelatedItem> required)
    {
        foreach (RelatedItem item in required) { if (!item.MatchOnEmpty) { return false; } }
        return true;
    }

    internal bool EvaluateRange(IReadOnlyList<ISerializableEntity> targets, IReadOnlyList<PropertyConditional> source, bool targetingContainer, bool or, int start)
    {
        for (int i = start; i < source.Count; i++)
        {
            if (EvaluateOne(source[i], targets, targetingContainer, or) == or) { return or; }
        }
        return !or;
    }

    internal bool EvaluateOne(PropertyConditional conditional, IReadOnlyList<ISerializableEntity> targets, bool targetingContainer, bool or)
    {
        if (!conditional.TargetContainer || targetingContainer) { return AnyMatches(targets, conditional); }
        Item item = OwnerItem(targets);
        if (item?.ParentInventory == null) { return conditional.ComparisonOperator == PropertyConditional.ComparisonOperatorType.NotEquals; }
        Entity owner = item.ParentInventory.Owner;
        if (conditional.TargetGrandParent && owner is Item parent) { owner = parent.ParentInventory?.Owner; }
        if (owner is Item container)
        {
            return conditional.Type == PropertyConditional.ConditionType.HasTag ? Match(conditional, container) : AnyMatches(container.AllPropertyObjects, conditional);
        }
        if (owner is Character character) { return Match(conditional, character); }
        return !or; // The original loop continues for an unsupported owner.
    }

    bool AnyMatches(IReadOnlyList<ISerializableEntity> targets, PropertyConditional conditional)
    {
        CompiledConditional predicate = CompiledConditional.Supports(conditional) ? Predicate(conditional) : null;
        bool any = string.IsNullOrEmpty(conditional.TargetItemComponent);
        for (int i = 0; i < targets.Count; i++)
        {
            ISerializableEntity target = targets[i];
            if ((any || target is ItemComponent component && component.Name == conditional.TargetItemComponent) &&
                (predicate != null ? predicate.Match(target) : conditional.Matches(target))) { return true; }
        }
        return false;
    }

    internal bool Match(PropertyConditional conditional, ISerializableEntity target)
    {
        return CompiledConditional.Supports(conditional) ? Predicate(conditional).Match(target) : conditional.Matches(target);
    }

    static Item OwnerItem(IReadOnlyList<ISerializableEntity> targets)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] is Item item) { return item; }
            if (targets[i] is ItemComponent component) { return component.Item; }
        }
        return null;
    }

    public void Clear()
    {
        last = null;
        programs.Clear();
        directPrograms.Clear();
        immutableSources.Clear();
        if (directDepth == 0) { directFrames.Clear(); }
        predicates.Clear();
        Epoch++;
        CompiledConditional.Epoch++;
        PropertyAccess.Clear();
    }

    public string Report() => $"predicates={predicates.Count} programs={ProgramCount} kernels={Kernels} evaluations={Evaluations} kernelCalls={KernelCalls} interpreted={InterpreterCalls} folded={Folded} duplicates={Duplicates} sharedReads={SharedReads} deopts={Deoptimizations} compileCost={Compilation}";
}
