using System;
using System.Reflection;
using Barotrauma;
using PrimaryPackage.Core;
using PrimaryPackage.Core.Fault;

namespace PrimaryPackage.Fault;

internal static class FaultHooks {
    public static readonly FaultLedger Ledger = new FaultLedger(Sink);

    private static readonly FaultBindings sites = new FaultBindings();
    private static FaultSite itemSite;
    private static ItemUpdatePolicy.Registration itemUpdates;

    public static void Bind(MethodBase method, FaultSite site) => sites.Bind(method, site);

    public static void ObserveItemUpdates(FaultSite site, MethodInfo prefix, MethodInfo finalizer) {
        itemSite = site;
        itemUpdates = ItemUpdatePolicy.Register(new[] { prefix, finalizer }, CanSkipIdleItem);
    }

    private static bool CanSkipIdleItem(object entity) => !Ledger.HasEntityFault(itemSite, entity);

    public static void ResetEntities() {
        Ledger.ResetEntities();
        itemUpdates?.Invalidate(null);
    }

    public static void Release() {
        itemUpdates?.Dispose();
        itemUpdates = null;
        itemSite = null;
        sites.Clear();
        Ledger.ResetAll();
    }

    public static bool GlobalPrefix(MethodBase __originalMethod) {
        if (Ledger.GlobalActive == 0) { return true; }
        return !sites.TryGet(__originalMethod, out FaultSite site) || Ledger.Enter(site, null, Timing.TotalTime);
    }

    public static Exception GlobalFinalizer(Exception __exception, MethodBase __originalMethod) {
        if (__exception == null) { return null; }
        return Contain(__originalMethod, null, __exception);
    }

    public static bool EntityPrefix(object __instance) {
        if (Ledger.EntityActive == 0) { return true; }
        if (!sites.TryGetEntity(__instance, out FaultSite site)) { return true; }
        bool run = Ledger.Enter(site, __instance, Timing.TotalTime, out bool recovered);
        if (recovered && ReferenceEquals(site, itemSite)) { itemUpdates?.Invalidate(__instance); }
        return run;
    }

    public static Exception EntityFinalizer(Exception __exception, object __instance) {
        if (__exception == null) { return null; }
        return sites.TryGetEntity(__instance, out FaultSite site) ? Contain(site, __instance, __exception) : __exception;
    }

    internal static Exception ContainBoundary(MethodBase method, Exception error) => Contain(method, null, error);

    internal static Exception Contain(MethodBase method, object entity, Exception error) {
        try { return sites.TryGet(method, out FaultSite site) ? Contain(site, entity, error) : error; }
        catch (Exception) { return error; }
    }

    private static Exception Contain(FaultSite site, object entity, Exception error) {
        try {
            if (!FaultClassifier.IsContainable(error)) { return error; }
            Ledger.Record(site, entity, entity == null ? null : Describe(entity), error, Timing.TotalTime);
            if (ReferenceEquals(site, itemSite)) { itemUpdates?.Invalidate(entity); }
            return null;
        } catch (Exception) {
            return error;
        }
    }

    private static string Describe(object entity) {
        try {
            switch (entity) {
                case Item item: return $"{item.Prefab?.Identifier.Value ?? "item"}#{item.ID}";
                case Character character: return $"{character.SpeciesName.Value}:{character.Name}#{character.ID}";
                case Entity other: return $"{other.GetType().Name}#{other.ID}";
                default: return entity.GetType().Name;
            }
        } catch (Exception) {
            return entity.GetType().Name;
        }
    }

    private static void Sink(FaultLevel level, string text) {
        switch (level) {
            case FaultLevel.Error: ModLog.Error(text); break;
            case FaultLevel.Warning: ModLog.Warning(text); break;
            default: ModLog.Message(text); break;
        }
    }
}
