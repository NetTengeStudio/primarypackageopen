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
        if (Active)
        {
            PerformanceFixBridge.Neutralize();
            MapEntity.MapEntityUpdateInterval = 2;
        }
    }

    protected override void OnToggled()
    {
        if (Active)
        {
            PerformanceFixBridge.Neutralize();
            MapEntity.MapEntityUpdateInterval = 2;
        }
        else
        {
            MapEntity.MapEntityUpdateInterval = 1;
        }
    }

    static void RoundStarted()
    {
        if (!On) { return; }
        System.Array.Clear(owners);
        MapEntity.MapEntityUpdateInterval = 2;
        PerformanceFixBridge.RoundStarted();
        lateNeutralize = LateNeutralizeTicks;
    }

    internal override void Uninstall()
    {
        MapEntity.MapEntityUpdateInterval = 1;
    }

    public static void BeginTick()
    {
        if (lateNeutralize > 0 && --lateNeutralize == 0) { PerformanceFixBridge.Neutralize(); }
        PerformanceFixBridge.Think();
    }

    public static bool IsEveryTick(int slot, Item item)
    {
        if (item.ParentInventory is CharacterInventory { Owner: Character owner } && owner.HasEquippedItem(item)) { return true; }
        if (item.body != null && item.body.Enabled && item.body.LinearVelocity.LengthSquared() > 0.05f) { return true; }
        if (slot >= owners.Length)
        {
            int size = System.Math.Max(slot + 1, owners.Length * 2);
            System.Array.Resize(ref owners, size);
            System.Array.Resize(ref classes, size);
        }
        if (!ReferenceEquals(owners[slot], item))
        {
            owners[slot] = item;
            classes[slot] = ClassifyItem(item) ? (byte)1 : (byte)0;
        }
        return classes[slot] != 0;
    }

    static bool ClassifyItem(Item item)
    {
        if (PerformanceFixBridge.Present && PerformanceFixBridge.IsStaticPriority(item)) { return true; }
        if (item.HasTag("highpriority".ToIdentifier())) { return true; }
        foreach (Barotrauma.Items.Components.ItemComponent c in item.Components)
        {
            switch (c)
            {
                case Barotrauma.Items.Components.Turret or Barotrauma.Items.Components.Engine or Barotrauma.Items.Components.Reactor or Barotrauma.Items.Components.Pump or Barotrauma.Items.Components.Sonar or Barotrauma.Items.Components.Controller or Barotrauma.Items.Components.Steering:
                case Barotrauma.Items.Components.RangedWeapon or Barotrauma.Items.Components.MeleeWeapon or Barotrauma.Items.Components.Projectile or Barotrauma.Items.Components.RepairTool:
                case Barotrauma.Items.Components.MotionSensor or Barotrauma.Items.Components.Door:
                    return true;
            }
        }
        return false;
    }

    public override string Report() =>
        $"performanceFix={(PerformanceFixBridge.Present ? $"replaced ({PerformanceFixBridge.Source}), hooks removed {PerformanceFixBridge.HooksRemoved}" : "absent")} " +
        $"interval={MapEntity.MapEntityUpdateInterval} everyTick={EveryTick} phased={Phased}";
}
