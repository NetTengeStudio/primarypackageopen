using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Op = Barotrauma.PropertyConditional.ComparisonOperatorType;
using Label = System.Reflection.Emit.Label;

namespace IEngine.StatusEffects;

// A guarded compiler: schema checks precede execution, and an opaque call can
// deoptimize only the remaining suffix. An already executed action is not replayed.
sealed class ConditionProgram
{
    const int MaxKernelTests = 2048;
    const int MaxKernelVariants = 64;
    readonly ConditionCompiler owner;
    readonly bool direct;
    readonly List<Kernel> kernels = new();
    int version;
    Kernel last;
    public readonly List<PropertyConditional> Source;

    public ConditionProgram(ConditionCompiler owner, List<PropertyConditional> source, bool direct = false)
    {
        this.owner = owner;
        Source = source;
        this.direct = direct;
        version = ConditionCompiler.Version(source);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEvaluate(IReadOnlyList<ISerializableEntity> targets, bool targetingContainer, bool or, out bool result, out bool pure)
    {
        result = false;
        pure = false;
        int current = ConditionCompiler.Version(Source);
        Kernel kernel = last;
        if (version != current || kernel == null || !kernel.Fits(targets, targetingContainer, or))
        {
            kernel = Resolve(targets, targetingContainer, or, current);
        }
        if (kernel?.Run == null) { return false; }
        result = kernel.Run(targets);
        pure = kernel.Pure && !kernel.Obsolete;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    Kernel Resolve(IReadOnlyList<ISerializableEntity> targets, bool targetingContainer, bool or, int current)
    {
        if (version != current) { kernels.Clear(); last = null; version = current; }
        if ((long)Source.Count * Math.Max(1, targets.Count) > MaxKernelTests) { return null; }
        Kernel kernel = null;
        for (int i = 0; i < kernels.Count; i++)
        {
            if (kernels[i].Fits(targets, targetingContainer, or)) { kernel = kernels[i]; break; }
        }
        if (kernel == null)
        {
            for (int i = kernels.Count - 1; i >= 0; i--)
            {
                if (kernels[i].SameLayout(targets, targetingContainer, or)) { kernels.RemoveAt(i); }
            }
            if (kernels.Count >= MaxKernelVariants) { kernels.RemoveAt(0); }
            long started = Stopwatch.GetTimestamp();
            kernel = new Kernel(owner, Source, targets, targetingContainer, or, direct);
            owner.Compilation.Add(Stopwatch.GetTimestamp() - started);
            kernels.Add(kernel);
        }
        last = kernel;
        return kernel;
    }

    sealed class ConditionTargetShape
    {
        public Type Type;
        public bool MapObserved, MapSafe, ComponentSafe;
        public Dictionary<Identifier, SerializableProperty> Map;
        public string Component;

        public ConditionTargetShape(ISerializableEntity target, bool needProperties, bool needComponent)
        {
            Type = target?.GetType();
            if (target == null) { return; }
            if (needProperties && PropertyAccess.CanCacheMap(Type))
            {
                MapObserved = true;
                Map = target.SerializableProperties;
                MapSafe = Map == null || ReferenceEquals(Map.Comparer, EqualityComparer<Identifier>.Default);
            }
            if (needComponent && target is ItemComponent component &&
                PropertyAccess.TryFieldGetter(Type.GetProperty(nameof(ItemComponent.Name))?.GetGetMethod(true), out _))
            {
                ComponentSafe = true;
                Component = component.Name;
            }
        }

        public bool Fits(ISerializableEntity target)
        {
            if (Type != target?.GetType() || ComponentSafe && (target is not ItemComponent component || Component != component.Name)) { return false; }
            if (!MapObserved) { return true; }
            Dictionary<Identifier, SerializableProperty> map = target.SerializableProperties;
            if (ReferenceEquals(map, Map)) { return true; }
            if (MapSafe != (map == null || ReferenceEquals(map.Comparer, EqualityComparer<Identifier>.Default))) { return false; }
            Map = map;
            return true;
        }

    }

    readonly record struct PropertyGuard(int Slot, Identifier Name, SerializableProperty Property, bool Exists, bool Affliction);
    readonly record struct ValueKey(int Slot, FieldInfo Field);
    readonly record struct ConditionKey(PropertyConditional.ConditionType Type, Identifier Name, string Value, Op Operator, string Component, bool Self);
    readonly record struct Test(int Slot, FieldInfo Field, float Number, Op Operator, bool Boolean, bool Expected);

    sealed class Clause
    {
        public PropertyConditional Source;
        public bool Opaque;
        public bool? Constant;
        public readonly List<int> Guards = new();
        public readonly List<Test> Tests = new();
    }

    sealed class Kernel
    {
        readonly ConditionCompiler owner;
        readonly List<PropertyConditional> source;
        readonly int version, epoch;
        readonly bool targetingContainer, or, array, direct;
        readonly ConditionTargetShape[] shapes;
        readonly PropertyGuard[] properties;
        public readonly Func<IReadOnlyList<ISerializableEntity>, bool> Run;
        public readonly bool Pure;
        public bool Obsolete { get; private set; }

        public Kernel(ConditionCompiler owner, List<PropertyConditional> source, IReadOnlyList<ISerializableEntity> targets, bool targetingContainer, bool or, bool direct)
        {
            this.owner = owner;
            this.source = source;
            this.targetingContainer = targetingContainer;
            this.or = or;
            this.direct = direct;
            version = ConditionCompiler.Version(source);
            epoch = owner.Epoch;
            array = targets is ISerializableEntity[];
            bool needProperties = false, needComponent = false;
            foreach (PropertyConditional conditional in source)
            {
                if (conditional == null) { continue; }
                needProperties |= conditional.Type == PropertyConditional.ConditionType.PropertyValueOrAffliction;
                needComponent |= !direct && !string.IsNullOrEmpty(conditional.TargetItemComponent);
            }
            shapes = new ConditionTargetShape[targets.Count];
            for (int i = 0; i < shapes.Length; i++) { shapes[i] = new ConditionTargetShape(targets[i], needProperties, needComponent); }
            var clauses = new Clause[source.Count];
            var guards = new Dictionary<PropertyGuard, int>();
            var guardList = new List<PropertyGuard>();
            bool useful = false;
            bool pure = true;
            for (int i = 0; i < clauses.Length; i++)
            {
                clauses[i] = Lower(source[i], targets, guards, guardList);
                useful |= !clauses[i].Opaque;
                pure &= !clauses[i].Opaque;
            }
            properties = guardList.ToArray();
            if (useful)
            {
                Run = Emit(clauses);
                Pure = pure;
                owner.Kernels++;
            }
        }

        public bool SameLayout(IReadOnlyList<ISerializableEntity> targets, bool container, bool logicalOr)
        {
            if (targetingContainer != container || or != logicalOr || array != (targets is ISerializableEntity[]) || shapes.Length != targets.Count) { return false; }
            for (int i = 0; i < shapes.Length; i++) { if (!shapes[i].Fits(targets[i])) { return false; } }
            return true;
        }

        public bool Fits(IReadOnlyList<ISerializableEntity> targets, bool container, bool logicalOr) =>
            !Obsolete && SameLayout(targets, container, logicalOr);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GuardProperty(int index)
        {
            ref readonly PropertyGuard guard = ref properties[index];
            Dictionary<Identifier, SerializableProperty> map = shapes[guard.Slot].Map;
            SerializableProperty property = null;
            bool found = map != null && map.TryGetValue(guard.Name, out property);
            bool binding = ReferenceEquals(property, guard.Property) || property != null && guard.Property != null &&
                property.Name == guard.Property.Name && property.PropertyType == guard.Property.PropertyType && Equals(property.PropertyInfo, guard.Property.PropertyInfo);
            if (found == guard.Exists && binding && AfflictionPrefab.Prefabs.ContainsKey(guard.Name) == guard.Affliction) { return true; }
            Obsolete = true;
            return false;
        }

        public bool Guard(IReadOnlyList<ISerializableEntity> targets) => epoch == owner.Epoch && version == ConditionCompiler.Version(source) && Fits(targets, targetingContainer, or);
        public bool Opaque(int position, IReadOnlyList<ISerializableEntity> targets) => direct ? owner.Match(source[position], targets[0]) : owner.EvaluateOne(source[position], targets, targetingContainer, or);
        public bool Continue(int position, IReadOnlyList<ISerializableEntity> targets)
        {
            owner.Deoptimizations++;
            return direct ? owner.EvaluateDirectRange(targets[0], source, or, position, version) : owner.EvaluateRange(targets, source, targetingContainer, or, position);
        }

        Clause Lower(PropertyConditional conditional, IReadOnlyList<ISerializableEntity> targets, Dictionary<PropertyGuard, int> guards, List<PropertyGuard> guardList)
        {
            var clause = new Clause { Source = conditional };
            if (conditional == null || conditional.TargetContainedItem || !direct && conditional.TargetContainer && !targetingContainer ||
                conditional.Type is not (PropertyConditional.ConditionType.PropertyValueOrAffliction or PropertyConditional.ConditionType.EntityType))
            {
                clause.Opaque = true;
                return clause;
            }
            bool matched = false;
            for (int slot = 0; slot < shapes.Length; slot++)
            {
                ConditionTargetShape shape = shapes[slot];
                ISerializableEntity target = targets[slot];
                if (!direct && !string.IsNullOrEmpty(conditional.TargetItemComponent))
                {
                    if (target is not ItemComponent) { continue; }
                    if (!shape.ComponentSafe) { clause.Opaque = true; continue; }
                    if (shape.Component != conditional.TargetItemComponent) { continue; }
                }
                bool notEquals = conditional.ComparisonOperator == Op.NotEquals;
                if (conditional.Type == PropertyConditional.ConditionType.EntityType)
                {
                    bool equals = conditional.AttributeValue.ToLowerInvariant() switch
                    {
                        "null" => target == null,
                        "item" => target is Item,
                        "character" => target is Character,
                        "limb" => target is Limb,
                        "structure" => target is Structure,
                        _ => false
                    };
                    matched |= equals ^ notEquals;
                    continue;
                }
                if (target == null) { matched |= notEquals; continue; }
                if (target is Limb || !shape.MapSafe) { clause.Opaque = true; continue; }
                SerializableProperty property = null;
                bool found = shape.Map != null && shape.Map.TryGetValue(conditional.AttributeName, out property);
                bool affliction = AfflictionPrefab.Prefabs.ContainsKey(conditional.AttributeName);
                var guard = new PropertyGuard(slot, conditional.AttributeName, property, found, affliction);
                if (!guards.TryGetValue(guard, out int index))
                {
                    index = guardList.Count;
                    guards.Add(guard, index);
                    guardList.Add(guard);
                }
                clause.Guards.Add(index);
                if (affliction)
                {
                    if (target is Character) { clause.Opaque = true; }
                    else { matched |= notEquals; }
                    continue;
                }
                if (property == null)
                {
                    if (found || target is Character) { clause.Opaque = true; }
                    else { matched |= notEquals; }
                    continue;
                }
                if (!PropertyAccess.Of(property).TryField(shape.Type, out FieldInfo field)) { clause.Opaque = true; continue; }
                if (field.FieldType == typeof(bool))
                {
                    if (conditional.ComparisonOperator is not (Op.Equals or Op.NotEquals)) { clause.Opaque = true; continue; }
                    clause.Tests.Add(new Test(slot, field, 0f, conditional.ComparisonOperator, true, conditional.AttributeValue.IsTrueString()));
                }
                else if (!conditional.FloatValue.HasValue) { matched |= notEquals; }
                else { clause.Tests.Add(new Test(slot, field, conditional.FloatValue.Value, conditional.ComparisonOperator, false, false)); }
            }
            if (!clause.Opaque && (matched || clause.Tests.Count == 0)) { clause.Constant = matched; clause.Tests.Clear(); }
            return clause;
        }

        Func<IReadOnlyList<ISerializableEntity>, bool> Emit(Clause[] clauses)
        {
            var method = new DynamicMethod("ie_logic", typeof(bool), new[] { typeof(Kernel), typeof(IReadOnlyList<ISerializableEntity>) }, typeof(ConditionProgram).Module, true);
            ILGenerator il = method.GetILGenerator();
            Label decisive = il.DefineLabel();
            var values = new Dictionary<ValueKey, LocalBuilder>();
            var known = new HashSet<ConditionKey>();
            var checkedGuards = new HashSet<int>();
            for (int i = 0; i < clauses.Length; i++)
            {
                Clause clause = clauses[i];
                if (clause.Opaque)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, AccessTools.Method(typeof(Kernel), nameof(Opaque)));
                    il.Emit(or ? OpCodes.Brtrue : OpCodes.Brfalse, decisive);
                    {
                        Label next = il.DefineLabel();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Call, AccessTools.Method(typeof(Kernel), nameof(Guard)));
                        il.Emit(OpCodes.Brtrue, next);
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldc_I4, i + 1);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Call, AccessTools.Method(typeof(Kernel), nameof(Continue)));
                        il.Emit(OpCodes.Ret);
                        il.MarkLabel(next);
                    }
                    values.Clear();
                    known.Clear();
                    checkedGuards.Clear();
                    continue;
                }
                PropertyConditional conditional = clause.Source;
                var key = new ConditionKey(conditional.Type, conditional.AttributeName, conditional.AttributeValue, conditional.ComparisonOperator, conditional.TargetItemComponent, conditional.TargetSelf);
                if (!known.Add(key)) { owner.Duplicates++; continue; }
                foreach (int index in clause.Guards)
                {
                    if (!checkedGuards.Add(index)) { continue; }
                    // Metadata checks follow short-circuit order as well. A
                    // failed binding resumes at this still-unexecuted clause.
                    Label valid = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, index);
                    il.Emit(OpCodes.Call, AccessTools.Method(typeof(Kernel), nameof(GuardProperty)));
                    il.Emit(OpCodes.Brtrue, valid);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, AccessTools.Method(typeof(Kernel), nameof(Continue)));
                    il.Emit(OpCodes.Ret);
                    il.MarkLabel(valid);
                }
                if (clause.Constant is bool constant)
                {
                    owner.Folded++;
                    if (constant == or) { il.Emit(OpCodes.Br, decisive); break; }
                    continue;
                }
                Label matched = or ? decisive : il.DefineLabel();
                Dictionary<ValueKey, LocalBuilder> definite = or ? null : new Dictionary<ValueKey, LocalBuilder>(values);
                foreach (Test test in clause.Tests)
                {
                    EmitTest(il, test, values);
                    il.Emit(OpCodes.Brtrue, matched);
                }
                if (!or)
                {
                    il.Emit(OpCodes.Br, decisive);
                    il.MarkLabel(matched);
                    // Any target may satisfy this clause. Only its first read is
                    // definite on every success path; later target reads may skip.
                    Test first = clause.Tests[0];
                    var firstKey = new ValueKey(first.Slot, first.Field);
                    definite.TryAdd(firstKey, values[firstKey]);
                    values = definite;
                }
            }
            il.Emit(or ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(decisive);
            il.Emit(or ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            return method.CreateDelegate<Func<IReadOnlyList<ISerializableEntity>, bool>>(this);
        }

        void EmitTest(ILGenerator il, Test test, Dictionary<ValueKey, LocalBuilder> values)
        {
            var key = new ValueKey(test.Slot, test.Field);
            if (!values.TryGetValue(key, out LocalBuilder value))
            {
                value = il.DeclareLocal(test.Boolean ? typeof(bool) : typeof(float));
                values.Add(key, value);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Castclass, array ? typeof(ISerializableEntity[]) : typeof(List<ISerializableEntity>));
                il.Emit(OpCodes.Ldc_I4, test.Slot);
                if (array) { il.Emit(OpCodes.Ldelem_Ref); }
                else { il.Emit(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(List<ISerializableEntity>), "Item")); }
                il.Emit(OpCodes.Castclass, test.Field.DeclaringType);
                il.Emit(OpCodes.Ldfld, test.Field);
                if (test.Field.FieldType == typeof(int)) { il.Emit(OpCodes.Conv_R4); }
                il.Emit(OpCodes.Stloc, value);
            }
            else { owner.SharedReads++; }
            il.Emit(OpCodes.Ldloc, value);
            if (test.Boolean)
            {
                il.Emit(test.Expected ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                if (test.Operator == Op.NotEquals) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); }
                return;
            }
            il.Emit(OpCodes.Ldc_R4, test.Number);
            switch (test.Operator)
            {
                case Op.GreaterThan: il.Emit(OpCodes.Cgt); break;
                case Op.LessThan: il.Emit(OpCodes.Clt); break;
                case Op.GreaterThanEquals:
                    il.Emit(OpCodes.Clt_Un); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); break;
                case Op.LessThanEquals:
                    il.Emit(OpCodes.Cgt_Un); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); break;
                default:
                    il.Emit(OpCodes.Ldc_I4, (int)test.Operator);
                    il.Emit(OpCodes.Call, AccessTools.Method(typeof(PropertyConditional), nameof(PropertyConditional.CompareFloat)));
                    break;
            }
        }
    }
}
