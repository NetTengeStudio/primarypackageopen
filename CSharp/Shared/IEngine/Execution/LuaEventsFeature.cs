using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using HarmonyLib;
using OneOf;

namespace IEngine.Execution;

sealed class LuaEventsFeature : Feature
{
    static LuaEventsFeature instance;
    static Func<ConcurrentDictionary<OneOf<IEvent, string>, IEvent>, bool> bucketsEmpty;
    static readonly EventService.TypeStringKey signalKey = new(typeof(IEventSignalReceived));
    static readonly EventService.TypeStringKey afflictionKey = new(typeof(IEventAfflictionUpdate));
    static readonly Dictionary<ItemPrefab, EventService.TypeStringKey> legacyKeys = new(ReferenceEqualityComparer.Instance);
    static MethodBase signalPost, afflictionPost;
    static string observers;
    static bool on, verify;
    static long skipped, delivered, verified, mismatches;

    public override string Name => "luaevents";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        signalPost = AccessTools.Method(typeof(HarmonyEventPatchesService), nameof(HarmonyEventPatchesService.Connection_SendSignal_Post));
        afflictionPost = AccessTools.Method(typeof(HarmonyEventPatchesService), nameof(HarmonyEventPatchesService.Affliction_Update_Post));
        MethodInfo empty = AccessTools.Method(typeof(ConcurrentDictionary<OneOf<IEvent, string>, IEvent>), "AreAllBucketsEmpty");
        if (signalPost == null || afflictionPost == null || empty == null) { return "LuaCs event patches have a different shape in this build"; }
        bucketsEmpty = empty.CreateDelegate<Func<ConcurrentDictionary<OneOf<IEvent, string>, IEvent>, bool>>();
        harmony.Patch(signalPost, prefix: new HarmonyMethod(typeof(LuaEventsFeature), nameof(SignalPrefix)) { priority = Priority.First });
        harmony.Patch(afflictionPost, prefix: new HarmonyMethod(typeof(LuaEventsFeature), nameof(AfflictionPrefix)) { priority = Priority.First });
        return null;
    }

    internal override void OnLoadCompleted() => Refresh();
    protected override void OnToggled() => Refresh();
    internal override void RecheckObservers() => Refresh();

    static void Refresh()
    {
        PatchCensus.Foreign a = PatchCensus.Of(signalPost), b = PatchCensus.Of(afflictionPost);
        observers = a.Any || b.Any ? $"{a}; {b}" : null;
        on = instance.Active && observers == null;
        legacyKeys.Clear();
    }

    static EventService Service()
    {
        if (HarmonyEventPatchesService._eventService is not EventService svc || svc.IsDisposed || svc._operationsLock._writeSemaphore.CurrentCount == 0) { return null; }
        return svc;
    }

    static bool Typed(EventService svc, in EventService.TypeStringKey key)
    {
        bool fast = svc._subscribers.TryGetValue(key, out var subs) && !bucketsEmpty(subs);
        if (verify) { Check(fast, svc._subscribers.TryGetValue(key, out var r) && !r.IsEmpty); }
        return fast;
    }

    static bool Legacy(EventService svc, ItemPrefab prefab)
    {
        if (!legacyKeys.TryGetValue(prefab, out EventService.TypeStringKey key))
        {
            key = new EventService.TypeStringKey("signalReceived." + prefab.Identifier);
            legacyKeys[prefab] = key;
        }
        bool fast = svc._luaLegacyEventsSubscribers.TryGetValue(key, out var subs) && !subs.IsEmpty;
        if (verify) { Check(fast, svc._luaLegacyEventsSubscribers.TryGetValue("signalReceived." + prefab.Identifier, out var r) && !r.IsEmpty); }
        return fast;
    }

    static void Check(bool fast, bool reference)
    {
        verified++;
        if (fast != reference) { mismatches++; }
    }

    static bool SignalPrefix(Connection __0, Signal __1)
    {
        if (!on) { return true; }
        EventService svc = Service();
        if (svc == null) { return true; }
        foreach (Wire wire in __0.Wires)
        {
            Connection recipient = wire.OtherConnection(__0);
            if (recipient != null) { Deliver(svc, __1, recipient); }
        }
        foreach (CircuitBoxConnection connection in __0.CircuitBoxConnections) { Deliver(svc, __1, connection.Connection); }
        return false;
    }

    static void Deliver(EventService svc, Signal signal, Connection recipient)
    {
        bool any = false;
        if (Typed(svc, signalKey))
        {
            any = true;
            svc.PublishEvent(delegate (IEventSignalReceived x) { x.OnSignalReceived(signal, recipient); });
        }
        if (Legacy(svc, recipient.Item.Prefab))
        {
            any = true;
            svc.Call("signalReceived." + recipient.Item.Prefab.Identifier, signal, recipient);
        }
        if (any) { delivered++; } else { skipped++; }
    }

    static bool AfflictionPrefix()
    {
        if (!on) { return true; }
        EventService svc = Service();
        if (svc == null || Typed(svc, afflictionKey)) { return true; }
        skipped++;
        return false;
    }

    public override void SetVerify(bool value)
    {
        verify = value;
        verified = mismatches = 0;
    }

    public override string Report() =>
        $"active={on} skipped={skipped} delivered={delivered}" + (observers != null ? $" disabled: observed by {observers}" : "") +
        (verify ? $" verified={verified} mismatches={mismatches}" : "");
}
