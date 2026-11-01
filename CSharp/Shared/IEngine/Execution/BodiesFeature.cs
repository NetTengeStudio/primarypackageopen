using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using FarseerPhysics.Dynamics;
using HarmonyLib;

namespace IEngine.Execution;

sealed class BodiesFeature : Feature
{
    static MethodInfo getList, forUpdate;
    static int sites;
    static bool verify;
    static long verifyTicks, uncovered;

    public override string Name => "bodies";

    protected override string Install(Harmony harmony)
    {
        getList = AccessTools.PropertyGetter(typeof(PhysicsBody), nameof(PhysicsBody.List));
        forUpdate = AccessTools.Method(typeof(BodyIndex), nameof(BodyIndex.ForUpdate));
        var screenUpdate = AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Update), new[] { typeof(double) });
        harmony.Patch(screenUpdate, transpiler: new HarmonyMethod(typeof(BodiesFeature), nameof(Transpiler)));
        if (sites != 2) { return $"expected 2 PhysicsBody.List loops in GameScreen.Update, found {sites}"; }
        var touch = new HarmonyMethod(typeof(BodiesFeature), nameof(Touched));
        foreach (ConstructorInfo ctor in typeof(PhysicsBody).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) { harmony.Patch(ctor, postfix: touch); }
        harmony.Patch(AccessTools.PropertySetter(typeof(PhysicsBody), nameof(PhysicsBody.Enabled)), postfix: touch);
        harmony.Patch(AccessTools.PropertySetter(typeof(PhysicsBody), nameof(PhysicsBody.UserData)), postfix: touch);
        harmony.Patch(AccessTools.PropertySetter(typeof(Body), nameof(Body.BodyType)), postfix: new HarmonyMethod(typeof(BodiesFeature), nameof(BodyTypeSet)));
        harmony.Patch(AccessTools.Method(typeof(PhysicsBody), nameof(PhysicsBody.Remove)), postfix: new HarmonyMethod(typeof(BodiesFeature), nameof(Removed)));
        harmony.Patch(AccessTools.Method(typeof(PhysicsBody), nameof(PhysicsBody.RemoveAll)), postfix: new HarmonyMethod(typeof(BodiesFeature), nameof(RemovedAll)));
        return null;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
    {
        sites = 0;
        foreach (CodeInstruction ci in code)
        {
            if (ci.Calls(getList))
            {
                sites++;
                yield return new CodeInstruction(OpCodes.Call, forUpdate) { labels = ci.labels, blocks = ci.blocks };
                continue;
            }
            yield return ci;
        }
    }

    static void Touched(PhysicsBody __instance) => BodyIndex.Touch(__instance);
    static void BodyTypeSet(Body __instance) => BodyIndex.TouchFarseer(__instance);
    static void Removed(PhysicsBody __instance) => BodyIndex.Forget(__instance);
    static void RemovedAll() => BodyIndex.Clear();

    internal override void OnLoadCompleted() => OnToggled();

    protected override void OnToggled()
    {
        if (Active && !BodyIndex.On) { BodyIndex.Start(); }
        if (!Active && BodyIndex.On) { BodyIndex.Stop(); }
    }

    public override void SetVerify(bool on)
    {
        verify = on;
        verifyTicks = uncovered = 0;
        if (on) { Diagnostics.Probes.SimTickEnded += VerifyTick; } else { Diagnostics.Probes.SimTickEnded -= VerifyTick; }
    }

    static void VerifyTick()
    {
        if (!BodyIndex.On) { return; }
        verifyTicks++;
        uncovered += BodyIndex.Uncovered(false);
    }

    public override string Report() =>
        $"candidates={BodyIndex.Count} of {PhysicsBody.List.Count} served={BodyIndex.Served} audits={BodyIndex.Audits} sweepMissed={BodyIndex.Missed}" +
        (verify ? $" verifyTicks={verifyTicks} uncovered={uncovered}" : "");
}
