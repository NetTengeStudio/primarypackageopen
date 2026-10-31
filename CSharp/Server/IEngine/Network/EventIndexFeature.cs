using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using IEngine.Core;
using IEngine.Execution;

namespace IEngine.Network;

sealed class EventIndexFeature : Feature
{
    enum Equality : byte { Identity, Value, Custom }

    static EventIndexFeature instance;
    static AccessTools.FieldRef<List<ServerEntityEvent>, int> version;
    static readonly EventDedupIndex<ServerEntityEvent> live = new(), unique = new();
    static readonly Dictionary<Type, Equality> equality = new();
    static MethodBase create, ctor;
    static ServerEntityEventManager owner;
    static int liveVersion = -1, uniqueVersion = -1;
    static double lastCreateTime;
    static bool on, verify, poisoned;
    static string observers;
    static long indexed, fallbacks, rebuilds, prunedPrefix, prunedScan, verified, mismatches;

    public override string Name => "netevents";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        create = AccessTools.Method(typeof(ServerEntityEventManager), nameof(ServerEntityEventManager.CreateEvent));
        ctor = AccessTools.Constructor(typeof(ServerEntityEvent), new[] { typeof(IServerSerializable), typeof(ushort) });
        MethodInfo clear = AccessTools.Method(typeof(ServerEntityEventManager), nameof(ServerEntityEventManager.Clear));
        if (create == null || ctor == null || clear == null) { return "ServerEntityEventManager has a different shape in this build"; }
        version = AccessTools.FieldRefAccess<List<ServerEntityEvent>, int>("_version");
        harmony.Patch(create, prefix: new HarmonyMethod(typeof(EventIndexFeature), nameof(CreatePrefix)) { priority = Priority.First });
        harmony.Patch(clear, postfix: new HarmonyMethod(typeof(EventIndexFeature), nameof(Cleared)));
        return null;
    }

    internal override void OnLoadCompleted() => Refresh();
    protected override void OnToggled() => Refresh();
    internal override void RecheckObservers() => Refresh();

    static void Refresh()
    {
        PatchCensus.Foreign a = PatchCensus.Of(create), b = PatchCensus.Of(ctor);
        observers = a.Any || b.Any ? $"{a}; {b}" : null;
        on = instance.Active && observers == null;
        Reset();
    }

    static void Cleared(ServerEntityEventManager __instance)
    {
        if (ReferenceEquals(__instance, owner)) { Reset(); }
    }

    static void Reset()
    {
        owner = null;
        live.Clear();
        unique.Clear();
        liveVersion = uniqueVersion = -1;
        poisoned = false;
    }

    static Equality EqualityOf(Type t)
    {
        if (equality.TryGetValue(t, out Equality e)) { return e; }
        Type declared = t.GetMethod(nameof(Equals), new[] { typeof(object) })?.DeclaringType;
        e = declared == typeof(object) ? Equality.Identity : declared == typeof(ValueType) ? Equality.Value : Equality.Custom;
        equality[t] = e;
        return e;
    }

    static bool TryKey(Entity entity, NetEntityEvent.IData data, out EventKey key)
    {
        if (data == null)
        {
            key = new EventKey(entity, null, null);
            return true;
        }
        Type t = data.GetType();
        switch (EqualityOf(t))
        {
            case Equality.Identity: key = new EventKey(entity, t, data); return true;
            case Equality.Value: key = new EventKey(entity, t, null); return true;
            default: key = default; return false;
        }
    }

    static bool Rebuild(ServerEntityEventManager manager)
    {
        rebuilds++;
        Reset();
        owner = manager;
        double last = double.NegativeInfinity;
        foreach (ServerEntityEvent e in manager.events)
        {
            if (!TryKey(e.Entity, e.Data, out EventKey key) || e.CreateTime < last) { poisoned = true; return false; }
            last = e.CreateTime;
            live.Add(key, e);
        }
        foreach (ServerEntityEvent e in manager.uniqueEvents)
        {
            if (!TryKey(e.Entity, e.Data, out EventKey key)) { poisoned = true; return false; }
            unique.Add(key, e);
        }
        lastCreateTime = last;
        Stamp(manager);
        return true;
    }

    static void Stamp(ServerEntityEventManager manager)
    {
        liveVersion = version(manager.events);
        uniqueVersion = version(manager.uniqueEvents);
    }

    static bool CreatePrefix(ServerEntityEventManager __instance, IServerSerializable entity, NetEntityEvent.IData extraData)
    {
        if (!on) { return true; }
        if (ReferenceEquals(__instance, owner) && poisoned) { fallbacks++; return true; }
        if (!ReferenceEquals(__instance, owner) || version(__instance.events) != liveVersion || version(__instance.uniqueEvents) != uniqueVersion)
        {
            if (!Rebuild(__instance)) { fallbacks++; return true; }
        }
        if (!TryKey(entity as Entity, extraData, out EventKey key))
        {
            poisoned = true;
            fallbacks++;
            return true;
        }
        if (!NetEntityEventManager.ValidateEntity(entity)) { return false; }
        GameServer server = __instance.server;
        List<ServerEntityEvent> events = __instance.events;
        bool inGame = false;
        foreach (Client c in server.ConnectedClients) { if (c.InGame) { inGame = true; break; } }
        if (GameMain.GameSession.RoundDuration > server.ServerSettings.RoundStartSyncDuration)
        {
            Prune(__instance, events, inGame, Timing.TotalTime - (double)server.ServerSettings.EventRemovalTime);
        }
        bool duplicate = HasUnsentDuplicate(live.Bucket(key), extraData);
        if (verify) { Check(duplicate, ReferenceUnsentDuplicate(events, entity as Entity, extraData)); }
        if (duplicate)
        {
            Stamp(__instance);
            return false;
        }
        __instance.ID++;
        var created = new ServerEntityEvent(entity, __instance.ID);
        if (extraData != null) { created.SetData(extraData); }
        if (created.CreateTime < lastCreateTime)
        {
            poisoned = true;
            events.Add(created);
            AddUnique(__instance, entity, extraData, key);
            return false;
        }
        lastCreateTime = created.CreateTime;
        events.Add(created);
        live.Add(key, created);
        indexed++;
        AddUnique(__instance, entity, extraData, key);
        Stamp(__instance);
        return false;
    }

    static void AddUnique(ServerEntityEventManager manager, IServerSerializable entity, NetEntityEvent.IData extraData, in EventKey key)
    {
        bool any = false;
        ReadOnlySpan<ServerEntityEvent> bucket = unique.Bucket(key);
        for (int i = 0; i < bucket.Length; i++)
        {
            if (Equals(bucket[i].Data, extraData)) { any = true; break; }
        }
        if (verify) { Check(any, ReferenceAny(manager.uniqueEvents, entity as Entity, extraData)); }
        if (any) { return; }
        var u = new ServerEntityEvent(entity, (ushort)(manager.uniqueEvents.Count + 1));
        u.SetData(extraData);
        manager.uniqueEvents.Add(u);
        unique.Add(key, u);
    }

    static bool HasUnsentDuplicate(ReadOnlySpan<ServerEntityEvent> bucket, NetEntityEvent.IData data)
    {
        for (int i = bucket.Length - 1; i >= 0; i--)
        {
            ServerEntityEvent e = bucket[i];
            if (!e.Sent && Equals(e.Data, data)) { return true; }
        }
        return false;
    }

    static void Prune(ServerEntityEventManager manager, List<ServerEntityEvent> events, bool inGame, double limit)
    {
        ushort lastSentToAll = manager.lastSentToAll;
        int expected = verify ? CountRemovable(events, inGame, lastSentToAll, limit) : -1;
        int n = events.Count, k = 0;
        while (k < n && Removable(events[k], inGame, lastSentToAll, limit)) { k++; }
        int removed;
        if (k < n && events[k].CreateTime < limit)
        {
            for (int i = 0; i < n; i++)
            {
                ServerEntityEvent e = events[i];
                if (Removable(e, inGame, lastSentToAll, limit) && TryKey(e.Entity, e.Data, out EventKey key)) { live.Remove(key, e); }
            }
            removed = events.RemoveAll(e => Removable(e, inGame, lastSentToAll, limit));
            prunedScan += removed;
        }
        else
        {
            for (int i = 0; i < k; i++)
            {
                ServerEntityEvent e = events[i];
                if (TryKey(e.Entity, e.Data, out EventKey key)) { live.Remove(key, e); }
            }
            if (k > 0) { events.RemoveRange(0, k); }
            removed = k;
            prunedPrefix += k;
        }
        if (verify) { Check(removed == expected, true); }
    }

    static bool Removable(ServerEntityEvent e, bool inGame, ushort lastSentToAll, double limit) =>
        (NetIdUtils.IdMoreRecent(lastSentToAll, e.ID) || !inGame) && e.CreateTime < limit;

    static int CountRemovable(List<ServerEntityEvent> events, bool inGame, ushort lastSentToAll, double limit)
    {
        int c = 0;
        foreach (ServerEntityEvent e in events) { if (Removable(e, inGame, lastSentToAll, limit)) { c++; } }
        return c;
    }

    static bool ReferenceUnsentDuplicate(List<ServerEntityEvent> events, Entity entity, NetEntityEvent.IData data)
    {
        for (int i = events.Count - 1; i >= 0; i--)
        {
            ServerEntityEvent e = events[i];
            if (e.Entity == entity && Equals(e.Data, data) && !e.Sent) { return true; }
        }
        return false;
    }

    static bool ReferenceAny(List<ServerEntityEvent> events, Entity entity, NetEntityEvent.IData data)
    {
        foreach (ServerEntityEvent e in events) { if (e.Entity == entity && Equals(e.Data, data)) { return true; } }
        return false;
    }

    static void Check(bool fast, bool reference)
    {
        verified++;
        if (fast != reference) { mismatches++; }
    }

    public override void SetVerify(bool value)
    {
        verify = value;
        verified = mismatches = 0;
    }

    public override string Report() =>
        $"active={on} indexed={indexed} fallbacks={fallbacks} rebuilds={rebuilds} poisoned={poisoned} live={live.Count}/{live.Buckets} unique={unique.Count}/{unique.Buckets} prunedPrefix={prunedPrefix} prunedScan={prunedScan}" +
        (observers != null ? $" disabled: observed by {observers}" : "") + (verify ? $" verified={verified} mismatches={mismatches}" : "");
}
