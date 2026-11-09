using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.Lights;
using HarmonyLib;
using IEngine.Execution;
using Microsoft.Xna.Framework;

namespace IEngine.Render;

sealed class ShadowQueriesFeature : Feature
{
    static ShadowQueriesFeature instance;
    static readonly List<ConvexHull> reusableHulls = new(512);
    static readonly MethodInfo original = AccessTools.Method(typeof(ConvexHull), nameof(ConvexHull.GetHullsInRange));
    static readonly MethodInfo dimensions = AccessTools.Method(typeof(ConvexHull), "CalculateDimensions");
    static readonly MethodInfo bounds = AccessTools.PropertyGetter(typeof(ConvexHull), nameof(ConvexHull.BoundingBox));
    static readonly MethodInfo[] callers = {
        AccessTools.Method(typeof(LightManager), nameof(LightManager.UpdateObstructVision)),
        AccessTools.Method(typeof(LightManager), nameof(LightManager.DebugDrawLos))
    };
    static int sites;
    static bool on;
    public override string Name => "shadowqueries";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        if (!NativeCode.Verified) { return "Native module identity changed"; }
        foreach (MethodInfo caller in callers)
        {
            harmony.Patch(caller, transpiler: new HarmonyMethod(typeof(ShadowQueriesFeature), nameof(Transpile)) { priority = Priority.Last });
        }
        harmony.Patch(dimensions, postfix: new HarmonyMethod(typeof(ShadowQueriesFeature), nameof(Changed)));
        return sites == 2 ? null : "Unexpected native shadow query calls: " + sites;
    }

    static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo query = AccessTools.Method(typeof(ShadowQueriesFeature), nameof(Query));
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                sites++;
                yield return new CodeInstruction(OpCodes.Call, query) { labels = instruction.labels, blocks = instruction.blocks };
            }
            else { yield return instruction; }
        }
    }

    static void Changed(ConvexHull __instance) => ShadowIndex.Changed(__instance);
    static void Refresh()
    {
        on = instance is { Active: true } && !PatchCensus.Of(original).Any && !PatchCensus.Of(dimensions).Any && !PatchCensus.Of(bounds).Any;
        foreach (MethodInfo caller in callers) { if (PatchCensus.Of(caller).Any) { on = false; } }
    }
    internal override void OnLoadCompleted() => Refresh();
    internal override void RecheckObservers() => Refresh();
    protected override void OnToggled() => Refresh();
    internal override void Uninstall() { on = false; sites = 0; ShadowIndex.Clear(); reusableHulls.Clear(); }

    static List<ConvexHull> Query(Vector2 position, float range, Submarine parentSubmarine)
    {
        if (!on) { return ConvexHull.GetHullsInRange(position, range, parentSubmarine); }
        reusableHulls.Clear();
        foreach (ConvexHullList group in ConvexHull.HullLists)
        {
            Vector2 lightPosition = position;
            if (parentSubmarine == null)
            {
                if (group.Submarine == null)
                {
                    ShadowIndex.Append(group.List, lightPosition, range, reusableHulls);
                }
                else
                {
                    Rectangle submarineBorders = group.Submarine.Borders;
                    submarineBorders.Y -= group.Submarine.Borders.Height;
                    if (!MathUtils.CircleIntersectsRectangle(lightPosition - group.Submarine.WorldPosition, range, submarineBorders)) { continue; }
                    lightPosition -= group.Submarine.WorldPosition - group.Submarine.HiddenSubPosition;
                    ShadowIndex.Append(group.List, lightPosition, range, reusableHulls);
                }
            }
            else
            {
                if (group.Submarine == null) { continue; }
                if (group.Submarine == parentSubmarine)
                {
                    ShadowIndex.Append(group.List, lightPosition, range, reusableHulls);
                }
                else
                {
                    lightPosition -= (group.Submarine.Position - parentSubmarine.Position);
                    Rectangle submarineBorders = group.Submarine.Borders;
                    submarineBorders.Location += group.Submarine.HiddenSubPosition.ToPoint() - new Point(0, group.Submarine.Borders.Height);
                    if (!MathUtils.CircleIntersectsRectangle(lightPosition, range, submarineBorders)) { continue; }
                    ShadowIndex.Append(group.List, lightPosition, range, reusableHulls);
                }
            }
        }
        return reusableHulls;
    }

    public override string Report() => $"active={on} queries={ShadowIndex.Queries} builds={ShadowIndex.Builds} refits={ShadowIndex.Refits} leafTests={ShadowIndex.Tested} rejectedNodes={ShadowIndex.Rejected}";
}
