using System;
using System.Diagnostics;
using Barotrauma;
using Barotrauma.Items.Components;
using IEngine.Core;
using IEngine.Diagnostics;
using IEngine.Simulation;

namespace IEngine.Execution;

static partial class MapEntityExecutor
{
    static readonly StableKeyOrder gapOrder = new();
    static Gap[] gapSnapshot = new Gap[512];

    public static bool ActiveSet;
    public static long ItemsVisited, ItemsUpdated;

    public static void Run(float deltaTime, Camera cam)
    {
        int tick = ++MapEntity.mapEntityUpdateTick;
        int interval = MapEntity.MapEntityUpdateInterval;
        bool full = tick % interval == 0;
        float stepped = deltaTime * interval;
        var rec = Probes.Recorder;

        long t0 = Stopwatch.GetTimestamp();
        if (full)
        {
            foreach (Hull hull in Hull.HullList) { hull.Update(stepped, cam); }
            UpdateCheats(stepped, cam);
            foreach (Structure wall in Structure.WallList) { wall.Update(stepped, cam); }
        }
        long t1 = Stopwatch.GetTimestamp();
        rec.AddTicks(Phase.Hulls, t1 - t0);

        UpdateGaps(deltaTime, cam);
        long t2 = Stopwatch.GetTimestamp();
        rec.AddTicks(Phase.Gaps, t2 - t1);

        if (tick % MapEntity.PoweredUpdateInterval == 0) { Powered.UpdatePower(deltaTime * MapEntity.PoweredUpdateInterval); }
        long t3 = Stopwatch.GetTimestamp();
        Counter("Update:MapEntity:Misc", t3 - t0);

        Item.UpdatePendingConditionUpdates(deltaTime);
        bool staggered = ActiveSet && interval > 1 && SchedulerFeature.On;
        if (SchedulerFeature.On) { SchedulerFeature.BeginTick(); }
        if (staggered) { UpdateItems(deltaTime, stepped, tick % interval, interval, cam); }
        else if (full) { UpdateItems(stepped, stepped, 0, 1, cam); }
        var priority = LuaCsSetup.Instance.Game.UpdatePriorityItems;
        if (priority.Count > 0)
        {
            foreach (Item item in priority)
            {
                if (item.Removed) { continue; }
                item.Update(deltaTime, cam);
            }
        }
        long t4 = Stopwatch.GetTimestamp();
        rec.AddTicks(Phase.Items, t4 - t3);
        Counter("Update:MapEntity:Items", t4 - t3);

        if (full)
        {
            UpdateSpriteStates(stepped);
            Entity.Spawner?.Update();
        }
        rec.AddTicks(Phase.SpriteStates, Stopwatch.GetTimestamp() - t4);
    }

    static void UpdateGaps(float deltaTime, Camera cam)
    {
        var gaps = Gap.GapList;
        foreach (Gap gap in gaps) { gap.ResetWaterFlowThisFrame(); }
        int n = gaps.Count;
        if (gapSnapshot.Length < n) { gapSnapshot = new Gap[Math.Max(n, gapSnapshot.Length * 2)]; }
        gaps.CopyTo(gapSnapshot);
        gapOrder.Reset(n);
        for (int i = 0; i < n; i++) { gapOrder.SetKey(i, Rand.Int(int.MaxValue)); }
        gapOrder.Sort();
        try
        {
            for (int p = 0; p < n; p++) { gapSnapshot[gapOrder.IndexAt(p)].Update(deltaTime, cam); }
        }
        finally
        {
            Array.Clear(gapSnapshot, 0, n);
        }
    }

    static void UpdateItems(float tickDt, float deltaTime, int phase, int interval, Camera cam)
    {
        bool staggered = interval > 1;
        var priority = LuaCsSetup.Instance.Game.UpdatePriorityItems;
        bool hasPriority = priority.Count > 0;
        Item last = null;
        try
        {
            if (!ActiveSet)
            {
                foreach (Item item in Item.ItemList)
                {
                    if (hasPriority && priority.Contains(item)) { continue; }
                    last = item;
                    item.Update(deltaTime, cam);
                }
                return;
            }
            ItemRegistry.BeginTick();
            SlotBitSet set = ItemRegistry.Active, dormant = Dormancy.Dormant, probe = Dormancy.Probe;
            bool containers = ContainerCore.On, verify = containers && ContainerCore.Verify;
            Dormancy.StepDt = deltaTime;
            long every = 0, phased = 0;
            if (SpatialCore.On) { SpatialCore.BeginTick(); }
            if (containers)
            {
                Dormancy.ProcessTimers();
                ContainerCore.BeginTick();
            }
            bool skipDormant = Dormancy.Count > 0 && !verify;
            long visited = 0, updated = 0;
            for (int s = skipDormant ? set.NextAndNot(dormant, 0) : set.Next(0); s >= 0; s = skipDormant ? set.NextAndNot(dormant, s + 1) : set.Next(s + 1))
            {
                visited++;
                Item item = ItemRegistry.Items[s];
                if (item == null || !item.IsActive)
                {
                    set.Clear(s);
                    continue;
                }
                if (hasPriority && priority.Contains(item)) { continue; }
                float dt = deltaTime;
                if (staggered)
                {
                    if (SchedulerFeature.IsEveryTick(s, item)) { dt = tickDt; every++; }
                    else if (s % interval != phase) { continue; }
                    else { phased++; }
                }
                last = item;
                updated++;
                if (containers)
                {
                    if (verify && dormant.Get(s))
                    {
                        if (Dormancy.ReasonsOf(s) == Dormancy.ByContainer) { ContainerCore.VerifyDormant(item, s, dt, cam); }
                        continue;
                    }
                    if (probe.Get(s)) { ContainerCore.Update(item, s, dt, cam); continue; }
                }
                if (ItemProfile.On)
                {
                    long t = Stopwatch.GetTimestamp();
                    item.Update(dt, cam);
                    ItemProfile.Add(item, Stopwatch.GetTimestamp() - t);
                    continue;
                }
                item.Update(dt, cam);
            }
            Dormancy.SimTime += staggered ? tickDt : deltaTime;
            SchedulerFeature.EveryTick += every;
            SchedulerFeature.Phased += phased;
            ItemProfile.EndItemTick();
            ItemsVisited += visited;
            ItemsUpdated += updated;
        }
        catch (InvalidOperationException e)
        {
            GameAnalyticsManager.AddErrorEventOnce(
                "MapEntity.UpdateAll:ItemUpdateInvalidOperation",
                GameAnalyticsManager.ErrorSeverity.Critical,
                $"Error while updating item {last?.Name ?? "null"}: {e.Message}");
            throw new InvalidOperationException($"Error while updating item {last?.Name ?? "null"}", innerException: e);
        }
    }

    static partial void UpdateCheats(float deltaTime, Camera cam);
    static partial void UpdateSpriteStates(float deltaTime);
    static partial void Counter(string key, long ticks);
}
