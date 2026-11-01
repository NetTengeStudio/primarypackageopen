using System.Reflection;
using Barotrauma;
using HarmonyLib;

namespace IEngine.Execution;

sealed class SchedulerFeature : Feature
{
    const int LateNeutralizeTicks = 240;

    static SchedulerFeature instance;
    static Item[] owners = new Item[8192];
    static byte[] classes = new byte[8192];
    static int lateNeutralize;
    public static long EveryTick, Phased;

    public override string Name => "scheduler";

    public static bool On => instance != null && instance.Active;

    protected override string Install(Harmony harmony)
    {
        instance = this;
        foreach (MethodInfo m in typeof(GameSession).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (m.Name == nameof(GameSession.StartRound)) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(SchedulerFeature), nameof(RoundStarted))); }
        }
        return null;
    }

    internal override void OnLoadCompleted()
    {
        PerformanceFixBridge.Detect();
        if (Active) { PerformanceFixBridge.Neutralize(); }
    }

    protected override void OnToggled()
    {
        if (Active) { PerformanceFixBridge.Neutralize(); }
    }

    static void RoundStarted()
    {
        if (!On) { return; }
        System.Array.Clear(owners);
        PerformanceFixBridge.RoundStarted();
        lateNeutralize = LateNeutralizeTicks;
    }

    public static void BeginTick()
    {
        if (lateNeutralize > 0 && --lateNeutralize == 0) { PerformanceFixBridge.Neutralize(); }
        PerformanceFixBridge.Think();
    }

    public static bool IsEveryTick(int slot, Item item)
    {
        if (!PerformanceFixBridge.Present) { return false; }
        if (PerformanceFixBridge.Hands && item.ParentInventory is CharacterInventory { Owner: Character owner } && owner.HasEquippedItem(item)) { return true; }
        if (slot >= owners.Length)
        {
            int size = System.Math.Max(slot + 1, owners.Length * 2);
            System.Array.Resize(ref owners, size);
            System.Array.Resize(ref classes, size);
        }
        if (!ReferenceEquals(owners[slot], item))
        {
            owners[slot] = item;
            classes[slot] = PerformanceFixBridge.IsStaticPriority(item) ? (byte)1 : (byte)0;
        }
        return classes[slot] != 0;
    }

    public override string Report() =>
        $"performanceFix={(PerformanceFixBridge.Present ? $"replaced ({PerformanceFixBridge.Source}), hooks removed {PerformanceFixBridge.HooksRemoved}" : "absent")} " +
        $"interval={MapEntity.MapEntityUpdateInterval} everyTick={EveryTick} phased={Phased}";
}
