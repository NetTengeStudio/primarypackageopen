using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Barotrauma;
using Op = Barotrauma.PropertyConditional.ComparisonOperatorType;

namespace IEngine.StatusEffects;

sealed class CompiledConditional
{
    enum Kind : byte { Original, Property, HasTag }
    enum Value : byte { Unresolved, Missing, Number, Bool, Other }

    public static int Epoch = 1;
    public static long Compiled, Fallbacks;

    public readonly PropertyConditional Source;
    readonly Kind kind;
    readonly Identifier name;
    readonly Op op;
    readonly bool notEquals, hasNumber, expectedBool;
    readonly float number;
    readonly ImmutableArray<Identifier> tags;

    sealed class TargetShape
    {
        public Dictionary<Identifier, SerializableProperty> Dict;
        public SerializableProperty Prop;
        public Value Value;
        public PropertyAccess.Binding Access;
    }

    const int MaxShapes = 8;
    int epoch;
    readonly TargetShape[] shapes = new TargetShape[MaxShapes];
    int shapeCount, nextVictim;
    TargetShape lastShape;

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

    public static bool Supports(PropertyConditional source) => !source.TargetContainedItem &&
        source.Type is PropertyConditional.ConditionType.PropertyValueOrAffliction or PropertyConditional.ConditionType.HasTag;

    public bool CanVerify(ISerializableEntity target)
    {
        if (kind != Kind.Property || target is Character or Limb or null) { return false; }
        Type type = target.GetType();
        if (!PropertyAccess.CanCacheMap(type)) { return false; }
        Dictionary<Identifier, SerializableProperty> map = target.SerializableProperties;
        if (map == null) { return true; }
        if (!ReferenceEquals(map.Comparer, EqualityComparer<Identifier>.Default)) { return false; }
        if (!map.TryGetValue(name, out SerializableProperty property)) { return true; }
        return property != null && PropertyAccess.Of(property).TryField(type, out _);
    }

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
        ImmutableArray<Identifier> t = tags;
        if (notEquals)
        {
            for (int i = 0; i < t.Length; i++) { if (item.HasTag(t[i])) { return false; } }
            return true;
        }
        else
        {
            for (int i = 0; i < t.Length; i++) { if (!item.HasTag(t[i])) { return false; } }
            return true;
        }
    }

    bool MatchProperty(ISerializableEntity target)
    {
        if (target == null) { return Source.Matches(null); }
        if (target is Character or Limb) { return Source.MatchesDirect(target); }
        Type type = target.GetType();
        if (!PropertyAccess.CanCacheMap(type)) { return Source.MatchesDirect(target); }
        Dictionary<Identifier, SerializableProperty> map = target.SerializableProperties;
        if (map == null || !ReferenceEquals(map.Comparer, EqualityComparer<Identifier>.Default))
        {
            return Source.MatchesDirect(target);
        }

        TargetShape shape = lastShape;
        if (shape == null || !ReferenceEquals(shape.Dict, map) || epoch != Epoch)
        {
            shape = LookupShape(map, type);
            if (shape == null) { return Source.MatchesDirect(target); }
            lastShape = shape;
        }

        switch (shape.Value)
        {
            case Value.Missing:
                return notEquals;
            case Value.Number:
                float current;
                if (!shape.Access.TryGetFloat(target, shape.Prop, out current)) { return Source.MatchesDirect(target); }
                return op switch
                {
                    Op.Equals => Math.Abs(current - number) < 0.00001f,
                    Op.NotEquals => Math.Abs(current - number) >= 0.00001f,
                    Op.GreaterThan => current > number,
                    Op.LessThan => current < number,
                    Op.GreaterThanEquals => current >= number,
                    Op.LessThanEquals => current <= number,
                    _ => Source.MatchesDirect(target)
                };
            case Value.Bool:
                bool flag;
                if (!shape.Access.TryGetBool(target, shape.Prop, out flag)) { return Source.MatchesDirect(target); }
                return notEquals ? flag != expectedBool : flag == expectedBool;
            default:
                return Source.MatchesDirect(target);
        }
    }

    TargetShape LookupShape(Dictionary<Identifier, SerializableProperty> map, Type type)
    {
        if (epoch != Epoch)
        {
            Array.Clear(shapes, 0, shapeCount);
            shapeCount = 0;
            nextVictim = 0;
            epoch = Epoch;
        }

        for (int i = 0; i < shapeCount; i++)
        {
            if (ReferenceEquals(shapes[i].Dict, map)) { return shapes[i]; }
        }

        var shape = new TargetShape { Dict = map };
        if (!map.TryGetValue(name, out SerializableProperty prop) || prop == null)
        {
            shape.Value = Value.Missing;
        }
        else
        {
            shape.Prop = prop;
            shape.Access = PropertyAccess.Of(prop);
            Type pType = prop.PropertyType;
            if (hasNumber && (pType == typeof(float) || pType == typeof(int)))
            {
                shape.Value = Value.Number;
            }
            else if (pType == typeof(bool))
            {
                shape.Value = Value.Bool;
            }
            else
            {
                shape.Value = Value.Other;
            }
        }

        if (shapeCount < MaxShapes)
        {
            shapes[shapeCount++] = shape;
        }
        else
        {
            shapes[nextVictim] = shape;
            nextVictim = (nextVictim + 1) % MaxShapes;
        }

        return shape;
    }
}
