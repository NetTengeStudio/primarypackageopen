using HarmonyLib;
using IEngine.Execution;

namespace IEngine.Simulation;

sealed class ContainerFeature : Feature
{
    static ContainerFeature instance;

    public override string Name => "containers";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        WakeHooks.Install(harmony);
        Dormancy.CatchUp = ContainerCore.CatchUp;
        return null;
    }

    public static void Refresh()
    {
        bool on = instance is { Active: true } && MapEntityExecutor.ActiveSet && ItemRegistry.Tracking;
        if (on == ContainerCore.On) { return; }
        ContainerCore.On = on;
        WakeHooks.Armed = on;
        if (!on) { Dormancy.WakeAll(Dormancy.ByContainer); }
        else { ContainerCore.Reset(); }
    }

    protected override void OnToggled() => Refresh();

    internal override void Uninstall()
    {
        ContainerCore.On = WakeHooks.Armed = false;
        Dormancy.WakeAll(Dormancy.ByContainer);
        EffectAnalysis.Clear();
    }

    public override void SetVerify(bool on) => ContainerCore.Verify = on;

    public override string Report() => ContainerCore.Report() + (WakeHooks.Problems.Count > 0 ? " hookProblems: " + string.Join("; ", WakeHooks.Problems) : "") + (ContainerCore.On ? "\n" + ContainerCore.Census() : "");
}
