using Barotrauma;
using HarmonyLib;

namespace IEngine.StatusEffects;

sealed class StatusTagFeature : Feature
{
    static readonly StatusTagIndex index = new();
    static StatusTagFeature instance;

    public static bool Verify;
    public static long Mismatches, Verified;

    public override string Name => "statustags";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        var matches = AccessTools.Method(typeof(PropertyConditional), "MatchesDirect");
        if (matches == null) { return "PropertyConditional.MatchesDirect not found"; }
        harmony.Patch(matches, prefix: new HarmonyMethod(typeof(StatusTagFeature), nameof(MatchesPrefix)) { priority = Priority.First });
        Suspend(harmony, AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.UpdateAll)));
        Suspend(harmony, AccessTools.Method(typeof(DelayedEffect), nameof(DelayedEffect.Update)));
        harmony.Patch(AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.AddTag)), postfix: new HarmonyMethod(typeof(StatusTagFeature), nameof(TagsChanged)));
        return null;
    }

    static void Suspend(Harmony harmony, System.Reflection.MethodBase method) =>
        harmony.Patch(method,
            prefix: new HarmonyMethod(typeof(StatusTagFeature), nameof(SuspendPrefix)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(StatusTagFeature), nameof(SuspendFinalizer)));

    static void SuspendPrefix() => index.Suspended++;

    static void SuspendFinalizer()
    {
        index.Suspended--;
        index.Invalidate();
    }

    static void TagsChanged() => index.InvalidateTags();

    static bool MatchesPrefix(PropertyConditional __instance, ISerializableEntity target, ref bool __result, bool __runOriginal)
    {
        if (!__runOriginal || __instance.Type != PropertyConditional.ConditionType.HasStatusTag || !instance.Active) { return true; }
        if (!index.TryMatch(__instance, target, out bool result)) { return true; }
        if (Verify)
        {
            Verified++;
            if (result != StatusTagReference.Matches(__instance, target)) { Mismatches++; }
        }
        __result = result;
        return false;
    }

    internal override void Uninstall() => index.Reset();

    public override void SetVerify(bool on)
    {
        Verify = on;
        Verified = Mismatches = 0;
    }

    public override string Report() => index.Report() + (Verify ? $" verified={Verified} mismatches={Mismatches}" : "");
}
