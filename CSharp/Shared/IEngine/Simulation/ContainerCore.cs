using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using IEngine.Execution;

namespace IEngine.Simulation;

static class ContainerCore
{
    const int CleanUpdatesToSleep = 2, DirtyUpdatesToGiveUp = 16;

    public static bool On, Verify;
    public static long Probes, Rejected, VerifyChecks, Mismatches;
    public static IEngine.Core.CostMeter TagDiffCost;
    static readonly Dictionary<string, int> blockers = new();
    static readonly Dictionary<string, int> mismatchPrefabs = new();
    static readonly List<ISerializableEntity> targets = new(), valid = new();
    static readonly IEngine.Core.ReferenceSnapshot<DurationListElement> durationTags = new();
    static readonly IEngine.Core.ReferenceSnapshot<DelayedListElement> delayTags = new();
    static readonly HarmonyLib.AccessTools.FieldRef<List<DurationListElement>, int> DurationVersion = HarmonyLib.AccessTools.FieldRefAccess<List<DurationListElement>, int>("_version");
    static readonly HarmonyLib.AccessTools.FieldRef<List<DelayedListElement>, int> DelayVersion = HarmonyLib.AccessTools.FieldRefAccess<List<DelayedListElement>, int>("_version");
    static int lastDurationVersion = int.MinValue, lastDelayVersion = int.MinValue;
    static readonly ActionType[] periodicContained = { ActionType.Always, ActionType.OnContained, ActionType.NotInWater };

    static readonly Func<CustomInterface, System.Collections.IList> GetCiElements = InitCiElementsGetter();
    static readonly Func<object, float> GetCiInterval = InitCiIntervalGetter();
    static readonly Func<object, bool> GetCiContinuous = InitCiContinuousGetter();
    static readonly Func<object, object> GetCiConnection = InitCiConnectionGetter();
    static readonly Func<object, int> GetCiEffectCount = InitCiEffectCountGetter();

    static Func<CustomInterface, System.Collections.IList> InitCiElementsGetter()
    {
        try
        {
            var field = typeof(CustomInterface).GetField("customInterfaceElementList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null) { return null; }
            var param = System.Linq.Expressions.Expression.Parameter(typeof(CustomInterface), "ci");
            var access = System.Linq.Expressions.Expression.Field(param, field);
            return System.Linq.Expressions.Expression.Lambda<Func<CustomInterface, System.Collections.IList>>(access, param).Compile();
        }
        catch { return null; }
    }

    static Func<object, float> InitCiIntervalGetter()
    {
        try
        {
            var elemType = typeof(CustomInterface).GetNestedType("CustomInterfaceElement", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var prop = elemType?.GetProperty("GetValueInterval");
            if (prop == null) { return null; }
            var param = System.Linq.Expressions.Expression.Parameter(typeof(object), "el");
            var cast = System.Linq.Expressions.Expression.Convert(param, elemType);
            var call = System.Linq.Expressions.Expression.Property(cast, prop);
            return System.Linq.Expressions.Expression.Lambda<Func<object, float>>(call, param).Compile();
        }
        catch { return null; }
    }

    static Func<object, bool> InitCiContinuousGetter()
    {
        try
        {
            var elemType = typeof(CustomInterface).GetNestedType("CustomInterfaceElement", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var field = elemType?.GetField("ContinuousSignal");
            if (field == null) { return null; }
            var param = System.Linq.Expressions.Expression.Parameter(typeof(object), "el");
            var cast = System.Linq.Expressions.Expression.Convert(param, elemType);
            var access = System.Linq.Expressions.Expression.Field(cast, field);
            return System.Linq.Expressions.Expression.Lambda<Func<object, bool>>(access, param).Compile();
        }
        catch { return null; }
    }

    static Func<object, object> InitCiConnectionGetter()
    {
        try
        {
            var elemType = typeof(CustomInterface).GetNestedType("CustomInterfaceElement", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var field = elemType?.GetField("Connection");
            if (field == null) { return null; }
            var param = System.Linq.Expressions.Expression.Parameter(typeof(object), "el");
            var cast = System.Linq.Expressions.Expression.Convert(param, elemType);
            var access = System.Linq.Expressions.Expression.Field(cast, field);
            return System.Linq.Expressions.Expression.Lambda<Func<object, object>>(System.Linq.Expressions.Expression.Convert(access, typeof(object)), param).Compile();
        }
        catch { return null; }
    }

    static Func<object, int> InitCiEffectCountGetter()
    {
        try
        {
            var elemType = typeof(CustomInterface).GetNestedType("CustomInterfaceElement", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var field = elemType?.GetField("StatusEffects");
            if (field == null) { return null; }
            var prop = field.FieldType.GetProperty("Count");
            var param = System.Linq.Expressions.Expression.Parameter(typeof(object), "el");
            var cast = System.Linq.Expressions.Expression.Convert(param, elemType);
            var access = System.Linq.Expressions.Expression.Field(cast, field);
            var count = System.Linq.Expressions.Expression.Property(access, prop);
            return System.Linq.Expressions.Expression.Lambda<Func<object, int>>(count, param).Compile();
        }
        catch { return null; }
    }

    static string CustomInterfaceBlocker(CustomInterface ci)
    {
        if (GetCiElements == null) { return "component CustomInterface"; }
        var list = GetCiElements(ci);
        if (list == null || list.Count == 0) { return null; }
        for (int i = 0; i < list.Count; i++)
        {
            object el = list[i];
            if (el == null) { continue; }
            if (GetCiInterval != null && GetCiInterval(el) > 0f) { return "CustomInterface polling"; }
            if (GetCiEffectCount != null && GetCiEffectCount(el) > 0) { return "CustomInterface status effect"; }
            if (GetCiContinuous != null && GetCiConnection != null && GetCiContinuous(el) && GetCiConnection(el) != null) { return "CustomInterface continuous signal"; }
        }
        return null;
    }

    public static void Update(Item item, int slot, float deltaTime, Camera cam)
    {
        if (item.ParentInventory is not ItemInventory)
        {
            Dormancy.Probe.Clear(slot);
            item.Update(deltaTime, cam);
            return;
        }
        Probes++;
        Dormancy.Probing = item;
        Dormancy.Dirty = false;
        try { item.Update(deltaTime, cam); }
        finally { Dormancy.Probing = null; }
        if (Dormancy.Dirty || item.Removed || !item.IsActive)
        {
            Dormancy.Streak(slot) = 0;
            if (++Dormancy.DirtyStreak(slot) >= DirtyUpdatesToGiveUp) { Dormancy.Probe.Clear(slot); }
            return;
        }
        if (++Dormancy.Streak(slot) < CleanUpdatesToSleep) { return; }
        wakeAfter = double.PositiveInfinity;
        string blocker = Blocker(item);
        if (blocker != null)
        {
            Rejected++;
            blockers[blocker] = blockers.GetValueOrDefault(blocker) + 1;
            Dormancy.Probe.Clear(slot);
            Dormancy.Streak(slot) = 0;
            return;
        }
        if (!Dormancy.TrySleep(slot, Dormancy.ByContainer, wakeAfter))
        {
            Rejected++;
            blockers["update observer"] = blockers.GetValueOrDefault("update observer") + 1;
            Dormancy.Probe.Clear(slot);
            Dormancy.Streak(slot) = 0;
        }
    }

    static double wakeAfter;

    public static void VerifyDormant(Item item, int slot, float deltaTime, Camera cam)
    {
        VerifyChecks++;
        Dormancy.Probing = item;
        Dormancy.Dirty = false;
        try { item.Update(deltaTime, cam); }
        finally { Dormancy.Probing = null; }
        if (!Dormancy.Dirty) { return; }
        Mismatches++;
        string key = item.Prefab.Identifier.Value;
        mismatchPrefabs[key] = mismatchPrefabs.GetValueOrDefault(key) + 1;
        Dormancy.WakeSlot(slot, item, Dormancy.All);
    }

    static string Blocker(Item item)
    {
        if (item.ParentInventory is not ItemInventory) { return "not in a container"; }
        if (item.body is { Enabled: true }) { return "physics body"; }
        if (item.isDroppedStackOwner) { return "dropped stack"; }
        if (item.impactQueue is { IsEmpty: false }) { return "impacts"; }
        if (item.aiTarget is { NeedsUpdate: true }) { return "ai target"; }
        if (item.connections != null)
        {
            foreach (Connection c in item.connections.Values)
            {
                if (c.wires.Count > 0 || c.CircuitBoxConnections.Count > 0 || c.Effects != null) { return "wired"; }
            }
        }
        for (Item c = item.Container; c != null; c = c.Container)
        {
            if (c.ParentInventory is CharacterInventory) { return "carried"; }
            if (c.Container == null && c.body is { Enabled: true }) { return "moving container"; }
        }
        if ((item.hasInWaterStatusEffects || item.hasNotInWaterStatusEffects) && !item.inWaterProofContainer) { return "water sensitive"; }
        if (LuaHooked(item)) { return "lua statusEffect.apply hook"; }
        foreach (ItemComponent ic in item.updateableComponents)
        {
#if CLIENT
            if ((ic.loopingSoundChannel != null && ic.loopingSoundChannel.IsPlaying) || (ic.playingOneshotSoundChannels != null && ic.playingOneshotSoundChannels.Count > 0)) { return $"{ic.GetType().Name} active sound"; }
#endif
            if (ic.IsActiveConditionals != null && !StableConditionals(ic.IsActiveConditionals, item)) { return $"{ic.GetType().Name} volatile IsActive conditionals"; }
            if (!(ic.IsActive || ic.UpdateWhenInactive)) { continue; }
            switch (ic)
            {
                case ItemContainer container:
                    if (container.hasSignalConnections) { return "container signals"; }
                    if (!container.alwaysContainedItemsSpawned && !string.IsNullOrEmpty(container.SpawnWithId)) { return "container spawn pending"; }
                    foreach (var active in container.activeContainedItems)
                    {
                        if (ContainedEffectBlocker(container, active) is { } b) { return b; }
                    }
                    break;
                case EqualsComponent or BooleanOperatorComponent or ArithmeticComponent or StringComponent or MemoryComponent or ColorComponent or NotComponent:
                    break;
                case CustomInterface ci:
                    if (CustomInterfaceBlocker(ci) is { } cib) { return cib; }
                    break;
                case Turret turret:
                    if (turret.AutoOperate || turret.ActiveUser != null) { return "active turret"; }
                    break;
                case Sonar sonar:
                    if (sonar.CurrentMode == Sonar.Mode.Active) { return "active sonar"; }
                    break;
                case LightComponent light:
                    if (light.IsOn) { return "light on"; }
                    break;
                default:
                    return $"component {ic.GetType().Name}";
            }
        }
        if (item.statusEffectLists == null) { return null; }
        foreach (ActionType type in periodicContained)
        {
            if (type == ActionType.NotInWater && !item.hasNotInWaterStatusEffects) { continue; }
            if (!item.statusEffectLists.TryGetValue(type, out List<StatusEffect> list)) { continue; }
            foreach (StatusEffect effect in list)
            {
                if (EffectBlocker(effect, item, BuildItemTargets(effect, item)) is { } b) { return b; }
            }
        }
        return null;
    }

    static string ContainedEffectBlocker(ItemContainer container, ItemContainer.ActiveContainedItem active)
    {
        StatusEffect effect = active.StatusEffect;
        if (active.ExcludeBroken && active.Item.Condition <= 0) { return null; }
        if (active.ExcludeFullCondition && active.Item.IsFullCondition) { return null; }
        targets.Clear();
        if (effect.HasTargetType(StatusEffect.TargetType.This)) { targets.AddRange(container.Item.AllPropertyObjects); }
        if (effect.HasTargetType(StatusEffect.TargetType.Contained)) { targets.AddRange(active.Item.AllPropertyObjects); }
        if (effect.type is not (ActionType.OnActive or ActionType.OnContaining or ActionType.OnWearing)) { return null; }
        if (effect is DelayedEffect) { return "contained delayed effect"; }
        return EffectBlocker(effect, container.Item, targets);
    }

    static List<ISerializableEntity> BuildItemTargets(StatusEffect effect, Item item)
    {
        targets.Clear();
        bool hasTargets = effect.TargetIdentifiers == null;
        if (effect.HasTargetType(StatusEffect.TargetType.Contained))
        {
            hasTargets |= ContainedTargetsFeature.Append(item, effect, targets);
        }
        if (!hasTargets) { return null; }
        if (effect.HasTargetType(StatusEffect.TargetType.This)) { targets.AddRange(item.AllPropertyObjects); }
        if (item.Container != null && effect.HasTargetType(StatusEffect.TargetType.Parent)) { targets.AddRange(item.Container.AllPropertyObjects); }
        return targets;
    }

    static string EffectBlocker(StatusEffect effect, Item entity, List<ISerializableEntity> effectTargets)
    {
        if (effect.Disabled) { return null; }
        if (effect.Interval > 0 && StatusEffectRef.IntervalTimers(effect) is { } timers && timers.TryGetValue(entity, out float timer) && timer > 0)
        {
            wakeAfter = Math.Min(wakeAfter, timer);
            return null;
        }
        EffectInfo info = EffectAnalysis.Of(effect);
        if (!info.Supported)
        {
            if (info.GateOnContainerOnly && effect is not DelayedEffect && StatusEffectRef.RequiredItems(effect) == null && ContainerGateFails(effect, entity)) { return null; }
            return $"effect {info.Blocker}";
        }
        if (effectTargets != null && StableReads(info, effect, entity, effectTargets) is { } volatileRead) { return $"volatile read {volatileRead}"; }
        if (info.PureSetter) { return null; }
        if (effectTargets == null || !effect.HasRequiredItems(entity)) { return null; }
        valid.Clear();
        foreach (ISerializableEntity t in effectTargets) { if (effect.IsValidTarget(t)) { valid.Add(t); } }
        if (effect.TargetIdentifiers != null && valid.Count == 0) { return null; }
        return effect.HasRequiredConditions(valid) ? "side effect gate open" : null;
    }

    static bool ContainerGateFails(StatusEffect effect, Item entity)
    {
        if (entity.Container == null) { return false; }
        valid.Clear();
        valid.AddRange(entity.AllPropertyObjects);
        return !effect.HasRequiredConditions(valid);
    }

    static string StableReads(EffectInfo info, StatusEffect effect, Item owner, List<ISerializableEntity> effectTargets)
    {
        foreach (PropertyConditional pc in info.PropertyReads)
        {
            if (pc.TargetContainer)
            {
                if (owner.Container is { } c) { foreach (var t in c.AllPropertyObjects) { if (!EffectAnalysis.StableRead(t, pc.AttributeName, owner)) { return $"{t.GetType().Name}.{pc.AttributeName}"; } } }
                continue;
            }
            foreach (ISerializableEntity t in effectTargets) { if (!EffectAnalysis.StableRead(t, pc.AttributeName, owner)) { return $"{t.GetType().Name}.{pc.AttributeName}"; } }
        }
        return null;
    }

    static bool StableConditionals(IEnumerable<PropertyConditional> conditionals, Item item)
    {
        foreach (PropertyConditional pc in conditionals)
        {
            if (pc.Type != PropertyConditional.ConditionType.PropertyValueOrAffliction || AfflictionPrefab.Prefabs.ContainsKey(pc.AttributeName)) { continue; }
            foreach (var t in item.AllPropertyObjects) { if (!EffectAnalysis.StableRead(t, pc.AttributeName, item)) { return false; } }
        }
        return true;
    }

    static bool LuaHooked(Item item) =>
        LuaCsSetup.Instance?.Hook is EventService events &&
        events._luaLegacyEventsSubscribers.TryGetValue("statusEffect.apply." + item.Prefab.Identifier, out var subs) && !subs.IsEmpty;

    public static void CatchUp(Item item, double slept)
    {
        if (slept <= 0 || Verify) { return; }
        float dt = (float)slept;
        if (item.statusEffectLists != null)
        {
            foreach (var list in item.statusEffectLists.Values) { foreach (StatusEffect e in list) { Advance(e, item, dt); } }
        }
        foreach (ItemComponent ic in item.updateableComponents)
        {
            switch (ic)
            {
                case EqualsComponent eq: Advance(eq.timeSinceReceived, dt); break;
                case BooleanOperatorComponent bo: Advance(bo.timeSinceReceived, dt); break;
                case ArithmeticComponent ar: Advance(ar.timeSinceReceived, dt); break;
                case StringComponent st: Advance(st.timeSinceReceived, dt); break;
                case ItemContainer container:
                    foreach (var active in container.activeContainedItems) { Advance(active.StatusEffect, item, dt); }
                    break;
            }
        }
    }

    static void Advance(float[] timers, float dt)
    {
        if (timers == null) { return; }
        for (int i = 0; i < timers.Length; i++) { timers[i] += dt; }
    }

    static void Advance(StatusEffect e, Item item, float dt)
    {
        if (e.Interval <= 0 || e.intervalTimers == null || !e.intervalTimers.TryGetValue(item, out float t) || t <= 0) { return; }
        e.intervalTimers[item] = t - dt;
    }

    public static void BeginTick()
    {
        var durations = StatusEffect.DurationList;
        var delays = DelayedEffect.DelayList;
        int dv = DurationVersion(durations);
        int lv = DelayVersion(delays);
        if (dv == lastDurationVersion && lv == lastDelayVersion) { return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (dv != lastDurationVersion)
        {
            durationTags.Update(durations, static e => e is { Parent.statusEffectTags.Count: > 0 },
                Dormancy.Count > 0 ? static e => WakeTargets(e.Targets) : null);
            lastDurationVersion = dv;
        }
        if (lv != lastDelayVersion)
        {
            delayTags.Update(delays, static e => e is { Parent.statusEffectTags.Count: > 0 },
                Dormancy.Count > 0 ? static e => WakeTargets(e.Targets) : null);
            lastDelayVersion = lv;
        }
        TagDiffCost.Add(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
    }

    static void WakeTargets(List<ISerializableEntity> list)
    {
        if (list == null) { return; }
        Dormancy.Source = 7;
        foreach (ISerializableEntity t in list)
        {
            switch (t)
            {
                case Item i:
                    if (ItemRegistry.TryGetSlot(i, out int s) && Dormancy.ReasonsOf(s) != 0) { WakeHooks.WakeNear(i); }
                    break;
                case ItemComponent c:
                    if (ItemRegistry.TryGetSlot(c.Item, out int cs) && Dormancy.ReasonsOf(cs) != 0) { WakeHooks.WakeNear(c.Item); }
                    break;
            }
        }
    }

    public static void Reset()
    {
        durationTags.Clear();
        delayTags.Clear();
        lastDurationVersion = lastDelayVersion = int.MinValue;
        blockers.Clear();
        mismatchPrefabs.Clear();
        Probes = Rejected = VerifyChecks = Mismatches = 0;
    }

    public static string Census()
    {
        var byBlocker = new Dictionary<string, int>();
        var byPrefab = new Dictionary<string, int>();
        int dormant = 0, awake = 0;
        Item[] items = ItemRegistry.Items;
        var active = ItemRegistry.Active;
        for (int s = active.Next(0); s >= 0; s = active.Next(s + 1))
        {
            if (items[s] is not { ParentInventory: ItemInventory } item) { continue; }
            if (Dormancy.Dormant.Get(s)) { dormant++; continue; }
            awake++;
            string b = Blocker(item) ?? "eligible, not settled";
            byBlocker[b] = byBlocker.GetValueOrDefault(b) + 1;
            string k = $"{item.Prefab.Identifier.Value} ({b})";
            byPrefab[k] = byPrefab.GetValueOrDefault(k) + 1;
        }
        var sb = new StringBuilder($"contained active items: dormant {dormant}, awake {awake}\n");
        foreach (var kv in byBlocker.OrderByDescending(k => k.Value)) { sb.Append($"  {kv.Value,5} {kv.Key}\n"); }
        sb.Append("awake contained by prefab:\n");
        foreach (var kv in byPrefab.OrderByDescending(k => k.Value).Take(40)) { sb.Append($"  {kv.Value,5} {kv.Key}\n"); }
        return sb.ToString();
    }

    public static string Report()
    {
        var sb = new StringBuilder();
        sb.Append($"{Dormancy.Report()} probes={Probes} rejected={Rejected} effectsAnalysed={EffectAnalysis.Analysed} tagDiffCost={TagDiffCost}");
        if (Verify) { sb.Append($" verifyChecks={VerifyChecks} mismatches={Mismatches}"); }
        if (mismatchPrefabs.Count > 0) { sb.Append(" mismatchPrefabs: ").Append(string.Join(", ", mismatchPrefabs.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}={k.Value}"))); }
        if (Dormancy.DirtyCauses.Count > 0) { sb.Append(" dirty: ").Append(string.Join(", ", Dormancy.DirtyCauses.OrderByDescending(k => k.Value).Take(15).Select(k => $"{k.Key}={k.Value}"))); }
        if (blockers.Count > 0) { sb.Append(" blockers: ").Append(string.Join(", ", blockers.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key}={k.Value}"))); }
        return sb.ToString();
    }
}
