using System;
using System.Globalization;
using HarmonyLib;
using IEngine.Execution;

namespace IEngine.Simulation;

sealed class SpatialFeature : Feature
{
    static SpatialFeature instance;

    public override string Name => "spatial";
    public override bool OptIn => true;

    protected override string Install(Harmony harmony)
    {
        instance = this;
        WakeHooks.Install(harmony);
        Configure();
        return null;
    }

    public static void Configure()
    {
        float radius = Settings.SpatialRadius;
        if (float.TryParse(Environment.GetEnvironmentVariable("IE_SPATIAL_RADIUS"), NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) { radius = r; }
        SpatialCore.Radius = radius * 100f;
        SpatialCore.Hysteresis = Settings.SpatialHysteresis * 100f;
        SpatialCore.NpcRadius = Settings.SpatialNpcRadius * 100f;
        SpatialCore.BotsAsViewers = Environment.GetEnvironmentVariable("IE_SPATIAL_BOTS") == "1";
    }

    public static void Refresh()
    {
        bool on = instance is { Active: true } && MapEntityExecutor.ActiveSet && ItemRegistry.Tracking;
        if (on == SpatialCore.On) { return; }
        SpatialCore.On = on;
        if (on) { SpatialCore.Start(); } else { SpatialCore.Stop(); }
    }

    protected override void OnToggled() => Refresh();

    internal override void Uninstall()
    {
        SpatialCore.On = false;
        SpatialCore.Stop();
    }

    public override string Report() => SpatialCore.Report();
}
