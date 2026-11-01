using System.Reflection;
using Barotrauma;
using HarmonyLib;

namespace IEngine.Execution;

sealed class ExecutorFeature : Feature
{
    static ExecutorFeature instance;
    static MethodInfo updateAll;
    static int recheckIn;
    static string blocked, conflict;

    public override string Name => "executor";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        updateAll = AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll));
        harmony.Patch(updateAll, prefix: new HarmonyMethod(typeof(ExecutorFeature), nameof(Prefix)) { priority = Priority.Last });
        return null;
    }

    static bool Prefix(float deltaTime, Camera cam, bool __runOriginal)
    {
        if (!__runOriginal || !instance.Active) { return true; }
        if (--recheckIn <= 0)
        {
            recheckIn = 600;
            Recheck();
        }
        if (blocked != null) { return true; }
        MapEntityExecutor.Run(deltaTime, cam);
        return false;
    }

    static void Recheck()
    {
        var foreign = PatchCensus.Of(updateAll);
        conflict = foreign.Transpilers.Length > 0 ? foreign.ToString() : null;
        blocked = conflict != null && !Settings.Takeover ? $"yielding to foreign transpiler on MapEntity.UpdateAll {conflict}" : null;
        ActiveSetFeature.Refresh();
    }

    protected override void OnToggled()
    {
        recheckIn = 0;
        ActiveSetFeature.Refresh();
    }

    public static void ForceRecheck() => recheckIn = 0;

    public override string Report() => blocked ?? (conflict != null ? $"running, took over from {conflict}" : $"running, UpdateAll patches: {PatchCensus.Of(updateAll)}");
}
