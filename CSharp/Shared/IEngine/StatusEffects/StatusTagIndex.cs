using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Barotrauma;
using HarmonyLib;

namespace IEngine.StatusEffects;

sealed class StatusTagIndex
{
    const int MaxTags = 64;

    static readonly AccessTools.FieldRef<List<DurationListElement>, int> DurationVersion = AccessTools.FieldRefAccess<List<DurationListElement>, int>("_version");
    static readonly AccessTools.FieldRef<List<DelayedListElement>, int> DelayVersion = AccessTools.FieldRefAccess<List<DelayedListElement>, int>("_version");

    readonly Dictionary<Identifier, int> bitOfTag = new();
    readonly List<Identifier> tags = new();
    readonly Dictionary<PropertyConditional, ulong[]> queries = new();
    readonly Dictionary<StatusEffect, ulong> parentMasks = new();
    readonly Dictionary<ISerializableEntity, ulong> masks = new(ReferenceEqualityComparer.Instance);

    int durationVersion = int.MinValue, delayVersion = int.MinValue, durationCount, delayCount;
    long epoch, builtEpoch = -1;

    public int Suspended;
    public long Queries, FullBuilds, Appends, Fallbacks;

    public void Invalidate() => epoch++;

    public void InvalidateTags()
    {
        parentMasks.Clear();
        epoch++;
    }

    public bool TryMatch(PropertyConditional conditional, ISerializableEntity target, out bool result)
    {
        result = false;
        if (Suspended != 0) { return false; }
        if (!queries.TryGetValue(conditional, out ulong[] bits) && !TryRegister(conditional, out bits))
        {
            Fallbacks++;
            return false;
        }
        Queries++;
        bool notEquals = conditional.ComparisonOperator == PropertyConditional.ComparisonOperatorType.NotEquals;
        if (target == null)
        {
            result = notEquals;
            return true;
        }
        Refresh();
        ulong mask = masks.TryGetValue(target, out ulong m) ? m : 0UL;
        int found = 0;
        foreach (ulong bit in bits) { if ((mask & bit) != 0) { found++; } }
        result = notEquals ? found < bits.Length : found >= bits.Length;
        return true;
    }

    bool TryRegister(PropertyConditional conditional, out ulong[] bits)
    {
        var tagList = conditional.AttributeValueAsTags;
        bits = new ulong[tagList.Length];
        for (int i = 0; i < tagList.Length; i++)
        {
            Identifier tag = tagList[i];
            if (!bitOfTag.TryGetValue(tag, out int bit))
            {
                if (tags.Count == MaxTags) { bits = null; return false; }
                bit = tags.Count;
                tags.Add(tag);
                bitOfTag.Add(tag, bit);
                parentMasks.Clear();
                builtEpoch = -1;
            }
            bits[i] = 1UL << bit;
        }
        queries.Add(conditional, bits);
        return true;
    }

    void Refresh()
    {
        var durations = StatusEffect.DurationList;
        var delays = DelayedEffect.DelayList;
        int dv = DurationVersion(durations), lv = DelayVersion(delays);
        if (builtEpoch == epoch && dv == durationVersion && lv == delayVersion) { return; }
        int dAdded = durations.Count - durationCount, lAdded = delays.Count - delayCount;
        if (builtEpoch == epoch && dAdded >= 0 && lAdded >= 0 && dv - durationVersion == dAdded && lv - delayVersion == lAdded)
        {
            for (int i = durationCount; i < durations.Count; i++) { Add(durations[i].Parent, durations[i].Targets); }
            for (int i = delayCount; i < delays.Count; i++) { Add(delays[i].Parent, delays[i].Targets); }
            Appends++;
        }
        else
        {
            masks.Clear();
            foreach (DurationListElement e in durations) { Add(e.Parent, e.Targets); }
            foreach (DelayedListElement e in delays) { Add(e.Parent, e.Targets); }
            FullBuilds++;
        }
        durationVersion = dv;
        delayVersion = lv;
        durationCount = durations.Count;
        delayCount = delays.Count;
        builtEpoch = epoch;
    }

    void Add(StatusEffect parent, List<ISerializableEntity> targets)
    {
        ulong pm = ParentMask(parent);
        if (pm == 0) { return; }
        foreach (ISerializableEntity t in targets)
        {
            if (t == null) { continue; }
            CollectionsMarshal.GetValueRefOrAddDefault(masks, t, out _) |= pm;
        }
    }

    ulong ParentMask(StatusEffect parent)
    {
        ref ulong cached = ref CollectionsMarshal.GetValueRefOrAddDefault(parentMasks, parent, out bool exists);
        if (exists) { return cached; }
        ulong m = 0;
        for (int i = 0; i < tags.Count; i++) { if (parent.HasTag(tags[i])) { m |= 1UL << i; } }
        cached = m;
        return m;
    }

    public string Report() =>
        $"tags={tags.Count} conditionals={queries.Count} queries={Queries} fullBuilds={FullBuilds} appends={Appends} fallbacks={Fallbacks} indexed={masks.Count} durations={StatusEffect.DurationList.Count} delays={DelayedEffect.DelayList.Count} popcount={PopCount()}";

    int PopCount()
    {
        int n = 0;
        foreach (ulong m in masks.Values) { n += BitOperations.PopCount(m); }
        return n;
    }

    public void Reset()
    {
        masks.Clear();
        parentMasks.Clear();
        builtEpoch = -1;
        Suspended = 0;
    }
}
