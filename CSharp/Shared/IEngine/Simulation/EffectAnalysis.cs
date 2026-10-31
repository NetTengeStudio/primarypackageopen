using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;

namespace IEngine.Simulation;

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
        info.GateOnContainerOnly = e.propertyConditionals is { Count: > 0 } pcs && pcs.TrueForAll(c => c.TargetContainer && c.Type is PropertyConditional.ConditionType.HasTag or PropertyConditional.ConditionType.HasStatusTag);
        if (e is DelayedEffect) { info.Blocker = "delayed"; return info; }
        if ((e.targetTypes & ~SupportedTargets) != 0) { info.Blocker = $"targets {e.targetTypes}"; return info; }
        if (e.luaHook != null) { info.Blocker = "luahook element"; return info; }
        if (e.lifeTime > 0) { info.Blocker = "lifetime"; return info; }
        if (e.requiredItems != null)
        {
            foreach (RelatedItem ri in e.requiredItems)
            {
                if (ri.Type != RelatedItem.RelationType.Contained) { info.Blocker = $"required item {ri.Type}"; return info; }
            }
        }
        var reads = new List<PropertyConditional>();
        if (e.propertyConditionals != null)
        {
            foreach (PropertyConditional pc in e.propertyConditionals)
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
        info.PureSetter = e.Duration <= 0 && e.setValue && !HasSideEffects(e) && (e.targetTypes & ~StatusEffect.TargetType.This) == 0;
        return info;
    }

    static bool HasSideEffects(StatusEffect e) =>
        e.useItemCount > 0 || e.removeItem || e.dropContainedItems || e.dropItem || e.removeCharacter || e.breakLimb || e.hideLimb ||
        e.explosions is { Count: > 0 } || e.spawnItems is { Count: > 0 } || e.spawnCharacters is { Count: > 0 } ||
        e.Afflictions.Count > 0 || e.ReduceAffliction.Count > 0 || e.aiTriggers is { Count: > 0 } || e.talentTriggers is { Count: > 0 } ||
        e.giveExperiences is { Count: > 0 } || e.giveSkills is { Count: > 0 } || e.giveTalentInfos is { Count: > 0 } || e.refundTalents ||
        e.triggeredEvents is { Count: > 0 } || e.eventTargetTags is { Count: > 0 } || e.UnlockRecipes is { Count: > 0 } || e.FireSize > 0 ||
        !e.forceSayIdentifier.IsEmpty || e.animationsToTrigger is { Count: > 0 } || e.randomCondition != Microsoft.Xna.Framework.Vector2.Zero ||
        e.oneShot || ClientSideEffects(e);

#if CLIENT
    static bool ClientSideEffects(StatusEffect e) => e.sounds.Count > 0 || e.particleEmitters is { Count: > 0 } || e.steamTimeLineEventToTrigger != default;
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
                sb.Append($"  [{kv.Key}] targets={e.targetTypes} interval={e.Interval} duration={e.Duration} setValue={e.setValue} disabled={e.Disabled} tags={e.Tags} range={e.Range}");
                sb.Append($" props=[{string.Join(",", System.Linq.Enumerable.Select(e.PropertyEffects, p => $"{p.propertyName}={p.value}"))}]");
                if (e.propertyConditionals != null) { sb.Append($" conds({e.conditionalLogicalOperator})=[{string.Join(",", e.propertyConditionals.ConvertAll(c => $"{c.Type}:{c.AttributeName} {c.ComparisonOperator} {c.AttributeValue}{(c.TargetContainer ? " container" : "")}"))}]"); }
                if (e.requiredItems != null) { sb.Append($" required=[{string.Join(",", e.requiredItems.ConvertAll(r => $"{r.Type}:{string.Join("|", r.Identifiers)}"))}]"); }
                sb.Append($" afflictions={e.Afflictions.Count} => supported={info.Supported} pure={info.PureSetter} watchTags={info.WatchTags} blocker={info.Blocker}");
                sb.Append(Environment.NewLine);
            }
        }
        return sb.ToString();
    }
}
