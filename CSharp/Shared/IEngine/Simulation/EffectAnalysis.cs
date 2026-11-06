using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace IEngine.Simulation;

internal static class StatusEffectRef
{
    public static readonly AccessTools.FieldRef<StatusEffect, StatusEffect.TargetType> TargetTypes =
        AccessTools.FieldRefAccess<StatusEffect, StatusEffect.TargetType>("targetTypes");

    public static readonly AccessTools.FieldRef<StatusEffect, List<RelatedItem>> RequiredItems =
        AccessTools.FieldRefAccess<StatusEffect, List<RelatedItem>>("requiredItems");

    public static readonly AccessTools.FieldRef<StatusEffect, List<PropertyConditional>> Conditionals =
        AccessTools.FieldRefAccess<StatusEffect, List<PropertyConditional>>("propertyConditionals");

    public static readonly AccessTools.FieldRef<StatusEffect, PropertyConditional.LogicalOperatorType> LogicalOp =
        AccessTools.FieldRefAccess<StatusEffect, PropertyConditional.LogicalOperatorType>("conditionalLogicalOperator");

    public static readonly AccessTools.FieldRef<StatusEffect, bool> SetValue =
        AccessTools.FieldRefAccess<StatusEffect, bool>("setValue");

    public static readonly AccessTools.FieldRef<StatusEffect, float> LifeTime =
        AccessTools.FieldRefAccess<StatusEffect, float>("lifeTime");

    public static readonly AccessTools.FieldRef<StatusEffect, Dictionary<Entity, float>> IntervalTimers =
        AccessTools.FieldRefAccess<StatusEffect, Dictionary<Entity, float>>("intervalTimers");

    public static readonly AccessTools.FieldRef<StatusEffect, bool> OneShot =
        AccessTools.FieldRefAccess<StatusEffect, bool>("oneShot");

    public static readonly AccessTools.FieldRef<StatusEffect, int> UseItemCount =
        AccessTools.FieldRefAccess<StatusEffect, int>("useItemCount");

    public static readonly AccessTools.FieldRef<StatusEffect, bool> RemoveItem =
        AccessTools.FieldRefAccess<StatusEffect, bool>("removeItem");

    public static readonly AccessTools.FieldRef<StatusEffect, bool> DropContainedItems =
        AccessTools.FieldRefAccess<StatusEffect, bool>("dropContainedItems");

    public static readonly AccessTools.FieldRef<StatusEffect, bool> DropItem =
        AccessTools.FieldRefAccess<StatusEffect, bool>("dropItem");

    public static readonly AccessTools.FieldRef<StatusEffect, bool> RemoveCharacter =
        AccessTools.FieldRefAccess<StatusEffect, bool>("removeCharacter");

    public static readonly AccessTools.FieldRef<StatusEffect, bool> BreakLimb =
        AccessTools.FieldRefAccess<StatusEffect, bool>("breakLimb");

    public static readonly AccessTools.FieldRef<StatusEffect, bool> HideLimb =
        AccessTools.FieldRefAccess<StatusEffect, bool>("hideLimb");

    public static readonly Func<StatusEffect, IList> SpawnItems = CreateListGetter("spawnItems");
    public static readonly Func<StatusEffect, IList> AiTriggers = CreateListGetter("aiTriggers");

    public static readonly AccessTools.FieldRef<StatusEffect, List<Identifier>> TalentTriggers =
        AccessTools.FieldRefAccess<StatusEffect, List<Identifier>>("talentTriggers");

    public static readonly AccessTools.FieldRef<StatusEffect, List<int>> GiveExperiences =
        AccessTools.FieldRefAccess<StatusEffect, List<int>>("giveExperiences");

    public static readonly Func<StatusEffect, IList> GiveSkills = CreateListGetter("giveSkills");

    public static readonly AccessTools.FieldRef<StatusEffect, List<EventPrefab>> TriggeredEvents =
        AccessTools.FieldRefAccess<StatusEffect, List<EventPrefab>>("triggeredEvents");

    public static readonly AccessTools.FieldRef<StatusEffect, Identifier> ForceSay =
        AccessTools.FieldRefAccess<StatusEffect, Identifier>("forceSayIdentifier");

    public static readonly AccessTools.FieldRef<StatusEffect, Microsoft.Xna.Framework.Vector2> RandomCondition =
        AccessTools.FieldRefAccess<StatusEffect, Microsoft.Xna.Framework.Vector2>("randomCondition");

    public static readonly Func<StatusEffect, IList> LuaHook = CreateListGetter("luaHook");
    public static readonly Func<StatusEffect, IList> Animations = CreateListGetter("animationsToTrigger");
    public static readonly Func<StatusEffect, IList> EventTargetTags = CreateListGetter("eventTargetTags");
#if CLIENT
    public static readonly Func<StatusEffect, IList> Sounds = CreateListGetter("sounds");
    public static readonly Func<StatusEffect, IList> ParticleEmitters = CreateListGetter("particleEmitters");
#endif

    private static Func<StatusEffect, IList> CreateListGetter(string fieldName)
    {
        try
        {
            var field = typeof(StatusEffect).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null) { return _ => null; }
            var param = System.Linq.Expressions.Expression.Parameter(typeof(StatusEffect), "e");
            var access = System.Linq.Expressions.Expression.Field(param, field);
            return System.Linq.Expressions.Expression.Lambda<Func<StatusEffect, IList>>(
                System.Linq.Expressions.Expression.Convert(access, typeof(IList)), param).Compile();
        }
        catch { return _ => null; }
    }
}

sealed class EffectInfo
{
    public bool Supported, PureSetter, WatchTags, GateOnContainerOnly;
    public PropertyConditional[] PropertyReads;
    public string Blocker;
}

static class EffectAnalysis
{
    const StatusEffect.TargetType SupportedTargets = StatusEffect.TargetType.This | StatusEffect.TargetType.Contained | StatusEffect.TargetType.Parent;

    static readonly Dictionary<StatusEffect, EffectInfo> effects = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<(Type, Identifier), bool> fieldBacked = new();

    public static int Analysed => effects.Count;

    public static EffectInfo Of(StatusEffect e)
    {
        if (effects.TryGetValue(e, out EffectInfo info)) { return info; }
        info = Analyse(e);
        effects.Add(e, info);
        return info;
    }

    public static void Clear()
    {
        effects.Clear();
        fieldBacked.Clear();
    }

    static EffectInfo Analyse(StatusEffect e)
    {
        var info = new EffectInfo();
        var conds = StatusEffectRef.Conditionals(e);
        info.GateOnContainerOnly = conds is { Count: > 0 } && conds.TrueForAll(c => c.TargetContainer && c.Type is PropertyConditional.ConditionType.HasTag or PropertyConditional.ConditionType.HasStatusTag);
        if (e is DelayedEffect) { info.Blocker = "delayed"; return info; }
        var targetTypes = StatusEffectRef.TargetTypes(e);
        if ((targetTypes & ~SupportedTargets) != 0) { info.Blocker = $"targets {targetTypes}"; return info; }
        if (StatusEffectRef.LuaHook(e) != null) { info.Blocker = "luahook element"; return info; }
        if (StatusEffectRef.LifeTime(e) > 0) { info.Blocker = "lifetime"; return info; }
        var required = StatusEffectRef.RequiredItems(e);
        if (required != null)
        {
            foreach (RelatedItem ri in required)
            {
                if (ri.Type != RelatedItem.RelationType.Contained) { info.Blocker = $"required item {ri.Type}"; return info; }
            }
        }
        var reads = new List<PropertyConditional>();
        if (conds != null)
        {
            foreach (PropertyConditional pc in conds)
            {
                switch (pc.Type)
                {
                    case PropertyConditional.ConditionType.HasStatusTag:
                        info.WatchTags = true;
                        break;
                    case PropertyConditional.ConditionType.PropertyValueOrAffliction:
                        if (!AfflictionPrefab.Prefabs.ContainsKey(pc.AttributeName)) { reads.Add(pc); }
                        break;
                    case PropertyConditional.ConditionType.SkillRequirement:
                    case PropertyConditional.ConditionType.HasSpecifierTag:
                        break;
                }
            }
        }
        info.PropertyReads = reads.ToArray();
        info.Supported = true;
        info.PureSetter = e.Duration <= 0 && StatusEffectRef.SetValue(e) && !HasSideEffects(e) && (targetTypes & ~StatusEffect.TargetType.This) == 0;
        return info;
    }

    static bool HasSideEffects(StatusEffect e)
    {
        if (StatusEffectRef.UseItemCount(e) > 0 || StatusEffectRef.RemoveItem(e) || StatusEffectRef.DropContainedItems(e) || StatusEffectRef.DropItem(e) ||
            StatusEffectRef.RemoveCharacter(e) || StatusEffectRef.BreakLimb(e) || StatusEffectRef.HideLimb(e))
        {
            return true;
        }

        if (System.Linq.Enumerable.Any(e.Explosions) || StatusEffectRef.SpawnItems(e) is { Count: > 0 } || System.Linq.Enumerable.Any(e.SpawnCharacters))
        {
            return true;
        }

        if (e.Afflictions.Count > 0 || e.ReduceAffliction.Count > 0 || StatusEffectRef.AiTriggers(e) is { Count: > 0 } || StatusEffectRef.TalentTriggers(e) is { Count: > 0 })
        {
            return true;
        }

        if (StatusEffectRef.GiveExperiences(e) is { Count: > 0 } || StatusEffectRef.GiveSkills(e) is { Count: > 0 } || e.giveTalentInfos is { Count: > 0 } || e.refundTalents)
        {
            return true;
        }

        if (StatusEffectRef.TriggeredEvents(e) is { Count: > 0 } || StatusEffectRef.EventTargetTags(e) is { Count: > 0 } || e.UnlockRecipes is { Count: > 0 } || e.FireSize > 0)
        {
            return true;
        }

        if (!StatusEffectRef.ForceSay(e).IsEmpty || StatusEffectRef.Animations(e) is { Count: > 0 } || StatusEffectRef.RandomCondition(e) != Microsoft.Xna.Framework.Vector2.Zero)
        {
            return true;
        }

        return StatusEffectRef.OneShot(e) || ClientSideEffects(e);
    }

#if CLIENT
    static bool ClientSideEffects(StatusEffect e) =>
        StatusEffectRef.Sounds(e) is { Count: > 0 } || StatusEffectRef.ParticleEmitters(e) is { Count: > 0 };
#else
    static bool ClientSideEffects(StatusEffect e) => false;
#endif

    public static bool StableRead(ISerializableEntity target, Identifier name, Item owner)
    {
        if (target?.SerializableProperties == null || !target.SerializableProperties.TryGetValue(name, out SerializableProperty property)) { return true; }
        if (!FieldBacked(target.GetType(), name, property)) { return false; }
        if (target is ItemComponent ic && ic.Item != owner && ic.Item.updateableComponents.Count > 0) { return false; }
        return true;
    }

    static bool FieldBacked(Type type, Identifier name, SerializableProperty property)
    {
        if (fieldBacked.TryGetValue((type, name), out bool v)) { return v; }
        v = IsFieldGetter(property.PropertyInfo?.GetGetMethod(true));
        fieldBacked.Add((type, name), v);
        return v;
    }

    static bool IsFieldGetter(MethodInfo getter)
    {
        if (getter == null || getter.IsVirtual && !getter.IsFinal) { return false; }
        byte[] il = getter.GetMethodBody()?.GetILAsByteArray();
        if (il == null) { return false; }
        int i = 0;
        while (i < il.Length && il[i] == 0x00) { i++; }
        return il.Length - i == 7 && il[i] == 0x02 && il[i + 1] == 0x7B && il[i + 6] == 0x2A;
    }
}

static class EffectDump
{
    public static string Of(string prefab)
    {
        Item item = null;
        foreach (Item i in Item.ItemList) { if (i.Prefab.Identifier.Value.Equals(prefab, StringComparison.OrdinalIgnoreCase)) { item = i; break; } }
        if (item == null) { return $"no item {prefab}"; }
        var sb = new System.Text.StringBuilder();
        sb.Append($"{prefab}: active={item.IsActive} parent={item.ParentInventory?.Owner} components={string.Join(",", item.updateableComponents.ConvertAll(c => $"{c.GetType().Name}(active={c.IsActive})"))}");
        sb.Append(Environment.NewLine);
        if (item.statusEffectLists == null) { return sb.Append("no effects").ToString(); }
        foreach (var kv in item.statusEffectLists)
        {
            foreach (StatusEffect e in kv.Value)
            {
                EffectInfo info = EffectAnalysis.Of(e);
                var conds = StatusEffectRef.Conditionals(e);
                var req = StatusEffectRef.RequiredItems(e);
                sb.Append($"  [{kv.Key}] targets={StatusEffectRef.TargetTypes(e)} interval={e.Interval} duration={e.Duration} setValue={StatusEffectRef.SetValue(e)} disabled={e.Disabled} tags={e.Tags} range={e.Range}");
                sb.Append($" props=[{string.Join(",", System.Linq.Enumerable.Select(e.PropertyEffects, p => $"{p.propertyName}={p.value}"))}]");
                if (conds != null) { sb.Append($" conds({StatusEffectRef.LogicalOp(e)})=[{string.Join(",", conds.ConvertAll(c => $"{c.Type}:{c.AttributeName} {c.ComparisonOperator} {c.AttributeValue}{(c.TargetContainer ? " container" : "")}"))}]"); }
                if (req != null) { sb.Append($" required=[{string.Join(",", req.ConvertAll(r => $"{r.Type}:{string.Join("|", r.Identifiers)}"))}]"); }
                sb.Append($" afflictions={e.Afflictions.Count} => supported={info.Supported} pure={info.PureSetter} watchTags={info.WatchTags} blocker={info.Blocker}");
                sb.Append(Environment.NewLine);
            }
        }
        return sb.ToString();
    }
}
