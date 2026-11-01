using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Op = Barotrauma.PropertyConditional.ComparisonOperatorType;

namespace IEngine.StatusEffects;

sealed class CompiledConditional
{
    enum Kind : byte { Original, Property, HasTag }
    enum Value : byte { Unresolved, Missing, Number, Bool, Other }

    static readonly Dictionary<PropertyInfo, Delegate> getters = new();
    public static int Epoch = 1;
    public static long Compiled, Fallbacks;

    public readonly PropertyConditional Source;
    readonly Kind kind;
    readonly Identifier name;
    readonly Op op;
    readonly bool notEquals, hasNumber, expectedBool;
    readonly float number;
    readonly ImmutableArray<Identifier> tags;

    sealed class Shape
    {
        public Dictionary<Identifier, SerializableProperty> Dict;
        public int Count;
        public SerializableProperty Prop;
        public Value Value;
        public Func<object, float> FloatGetter;
        public Func<object, bool> BoolGetter;
    }

    const int MaxShapes = 8;
    int epoch;
    bool isAffliction;
    readonly Shape[] shapes = new Shape[MaxShapes];
    int shapeCount, nextVictim;

    public CompiledConditional(PropertyConditional c)
    {
        Source = c;
        op = c.ComparisonOperator;
        notEquals = op == Op.NotEquals;
        name = c.AttributeName;
        tags = c.AttributeValueAsTags;
        hasNumber = c.FloatValue.HasValue;
        number = c.FloatValue ?? 0f;
        expectedBool = c.AttributeValue.IsTrueString();
        kind = c.TargetContainedItem ? Kind.Original : c.Type switch
        {
            PropertyConditional.ConditionType.PropertyValueOrAffliction => Kind.Property,
            PropertyConditional.ConditionType.HasTag => Kind.HasTag,
            _ => Kind.Original
        };
        if (kind == Kind.Original) { Fallbacks++; } else { Compiled++; }
    }

    public bool IsCompiled => kind != Kind.Original;

    public bool Match(ISerializableEntity target)
    {
        switch (kind)
        {
            case Kind.Property: return MatchProperty(target);
            case Kind.HasTag: return MatchTags(target);
            default: return Source.Matches(target);
        }
    }

    bool MatchTags(ISerializableEntity target)
    {
        if (target is Character or Limb or null) { return Source.MatchesDirect(target); }
        if (target is not Item item) { return notEquals; }
        int matches = 0;
        ImmutableArray<Identifier> t = tags;
        for (int i = 0; i < t.Length; i++) { if (item.HasTag(t[i])) { matches++; } }
        return notEquals ? matches <= 0 : matches >= t.Length;
    }

    bool MatchProperty(ISerializableEntity target)
    {
        if (target is Character or Limb or null) { return Source.MatchesDirect(target); }
        if (epoch != Epoch)
        {
            epoch = Epoch;
            isAffliction = AfflictionPrefab.Prefabs.ContainsKey(name);
            Array.Clear(shapes);
            shapeCount = nextVictim = 0;
        }
        if (isAffliction) { return notEquals; }
        Dictionary<Identifier, SerializableProperty> props = target.SerializableProperties;
        if (props == null) { return notEquals; }
        Shape s = ShapeOf(props);
        switch (s.Value)
        {
            case Value.Missing: return notEquals;
            case Value.Number:
                if (!hasNumber) { return notEquals; }
                return PropertyConditional.CompareFloat(ReadFloat(s, target), number, op);
            case Value.Bool:
                bool equal = ReadBool(s, target) == expectedBool;
                return notEquals ? !equal : equal;
            default: return Source.MatchesDirect(target);
        }
    }

    Shape ShapeOf(Dictionary<Identifier, SerializableProperty> props)
    {
        Shape[] a = shapes;
        for (int i = 0; i < shapeCount; i++)
        {
            Shape s = a[i];
            if (ReferenceEquals(s.Dict, props))
            {
                if (s.Count != props.Count) { Resolve(s, props); }
                return s;
            }
        }
        Shape fresh = new Shape();
        Resolve(fresh, props);
        if (shapeCount < MaxShapes) { a[shapeCount++] = fresh; }
        else
        {
            a[nextVictim] = fresh;
            nextVictim = (nextVictim + 1) % MaxShapes;
        }
        return fresh;
    }

    void Resolve(Shape s, Dictionary<Identifier, SerializableProperty> props)
    {
        s.Dict = props;
        s.Count = props.Count;
        s.FloatGetter = null;
        s.BoolGetter = null;
        if (!props.TryGetValue(name, out s.Prop))
        {
            s.Prop = null;
            s.Value = Value.Missing;
            return;
        }
        Type t = s.Prop.PropertyType;
        PropertyInfo info = s.Prop.PropertyInfo;
        if (info == null || info.GetGetMethod(true) == null || info.DeclaringType == null || info.DeclaringType.IsValueType) { s.Value = Value.Other; return; }
        if (t == typeof(float) || t == typeof(int))
        {
            s.Value = Value.Number;
            s.FloatGetter = (Func<object, float>)GetterOf(info, typeof(float));
        }
        else if (op is Op.Equals or Op.NotEquals && t == typeof(bool))
        {
            s.Value = Value.Bool;
            s.BoolGetter = (Func<object, bool>)GetterOf(info, typeof(bool));
        }
        else { s.Value = Value.Other; }
    }

    static float ReadFloat(Shape s, ISerializableEntity target)
    {
        try { return s.FloatGetter(target); }
        catch (Exception) { return s.Prop.GetFloatValue(target); }
    }

    static bool ReadBool(Shape s, ISerializableEntity target)
    {
        try { return s.BoolGetter(target); }
        catch (Exception) { return s.Prop.GetBoolValue(target); }
    }

    static Delegate GetterOf(PropertyInfo info, Type result)
    {
        lock (getters)
        {
            if (getters.TryGetValue(info, out Delegate d)) { return d; }
            var dm = new DynamicMethod($"ie_get_{info.DeclaringType.Name}_{info.Name}", result, new[] { typeof(object) }, typeof(CompiledConditional).Module, skipVisibility: true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, info.DeclaringType);
            il.Emit(OpCodes.Callvirt, info.GetGetMethod(true));
            if (info.PropertyType == typeof(int)) { il.Emit(OpCodes.Conv_R4); }
            il.Emit(OpCodes.Ret);
            d = dm.CreateDelegate(result == typeof(float) ? typeof(Func<object, float>) : typeof(Func<object, bool>));
            getters[info] = d;
            return d;
        }
    }
}
