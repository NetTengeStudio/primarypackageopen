using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace IEngine.Execution;

sealed class ComponentPlansFeature : Feature
{
    static ComponentPlansFeature instance;
    static MethodInfo shouldApply, applyList, delayedApplyList, containerUpdate;
    static int gateSites;
    static string observers;
    static bool on;
    static long skipped, passed;

    public override string Name => "plans";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        containerUpdate = AccessTools.Method(typeof(ItemContainer), nameof(ItemContainer.Update));
        shouldApply = AccessTools.Method(typeof(ItemContainer), "ShouldApplyEffects");
        var listArgs = new[] { typeof(ActionType), typeof(float), typeof(Entity), typeof(IReadOnlyList<ISerializableEntity>), typeof(Microsoft.Xna.Framework.Vector2?) };
        applyList = AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.Apply), listArgs);
        delayedApplyList = AccessTools.DeclaredMethod(typeof(DelayedEffect), nameof(DelayedEffect.Apply), listArgs);
        if (containerUpdate == null || shouldApply == null || applyList == null || delayedApplyList == null) { return "ItemContainer/StatusEffect shape differs in this build"; }
        harmony.Patch(containerUpdate, transpiler: new HarmonyMethod(typeof(ComponentPlansFeature), nameof(ContainerTranspiler)));
        return gateSites == 1 ? null : $"ItemContainer.Update has {gateSites} ShouldApplyEffects calls, expected 1";
    }

    internal override void OnLoadCompleted() => Refresh();
    protected override void OnToggled() => Refresh();
    internal override void RecheckObservers() => Refresh();

    static void Refresh()
    {
        var found = new List<string>();
        foreach (MethodBase m in new MethodBase[] { shouldApply, applyList, delayedApplyList })
        {
            PatchCensus.Foreign f = PatchCensus.Of(m);
            if (f.Any) { found.Add($"{m.DeclaringType?.Name}.{m.Name}: {f}"); }
        }
        observers = found.Count > 0 ? string.Join("; ", found) : null;
        on = instance.Active && observers == null;
    }

    static IEnumerable<CodeInstruction> ContainerTranspiler(IEnumerable<CodeInstruction> code)
    {
        MethodInfo gate = AccessTools.Method(typeof(ComponentPlansFeature), nameof(ContainedGate));
        foreach (CodeInstruction ci in code)
        {
            if (ci.Calls(shouldApply))
            {
                gateSites++;
                yield return new CodeInstruction(OpCodes.Call, gate) { labels = ci.labels, blocks = ci.blocks };
                continue;
            }
            yield return ci;
        }
    }

    static bool ContainedGate(ItemContainer container, ItemContainer.ActiveContainedItem contained)
    {
        if (on)
        {
            StatusEffect effect = contained.StatusEffect;
            System.Type t = effect.GetType();
            if ((t == typeof(StatusEffect) || t == typeof(DelayedEffect)) && effect.type is not (ActionType.OnActive or ActionType.OnContaining or ActionType.OnWearing))
            {
                skipped++;
                return false;
            }
            passed++;
        }
        return container.ShouldApplyEffects(contained);
    }

    public override string Report() =>
        $"active={on} containedSkipped={skipped} containedPassed={passed}" + (observers != null ? $" disabled: observed by {observers}" : "");
}
