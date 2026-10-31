using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using IEngine.Core;

namespace IEngine.Execution;

sealed class ListRemovalFeature : Feature
{
    static ListRemovalFeature instance;
    static readonly List<string> patched = new();
    public static long Fast, Slow;

    public override string Name => "lists";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        foreach (MethodBase m in new MethodBase[]
        {
            AccessTools.Method(typeof(Item), "RemoveFromLists"),
            AccessTools.Method(typeof(MapEntity), nameof(MapEntity.Remove)),
            AccessTools.Method(typeof(MapEntity), nameof(MapEntity.ShallowRemove)),
            AccessTools.Method(typeof(PhysicsBody), nameof(PhysicsBody.Remove))
        })
        {
            if (m == null) { continue; }
            harmony.Patch(m, transpiler: new HarmonyMethod(typeof(ListRemovalFeature), nameof(Transpiler)));
            patched.Add($"{m.DeclaringType?.Name}.{m.Name}");
        }
        return patched.Count == 0 ? "no removal methods found" : null;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
    {
        foreach (CodeInstruction ci in code)
        {
            if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo m && m.Name == nameof(List<object>.Remove) &&
                m.DeclaringType is { IsGenericType: true } t && t.GetGenericTypeDefinition() == typeof(List<>) && m.GetParameters().Length == 1 &&
                !t.GetGenericArguments()[0].IsValueType)
            {
                MethodInfo fast = AccessTools.Method(typeof(ListRemovalFeature), nameof(Remove)).MakeGenericMethod(t.GetGenericArguments()[0]);
                yield return new CodeInstruction(OpCodes.Call, fast) { labels = ci.labels, blocks = ci.blocks };
                continue;
            }
            yield return ci;
        }
    }

    static bool Remove<T>(List<T> list, T item) where T : class
    {
        if (instance.Active)
        {
            Fast++;
            return ListOps.RemoveReference(list, item);
        }
        Slow++;
        return list.Remove(item);
    }

    public override string Report() => $"patched=[{string.Join(", ", patched)}] fast={Fast} slow={Slow}";
}
