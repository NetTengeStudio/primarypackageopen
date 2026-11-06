using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace INet;

public static class TypedOrdinal {
    private static readonly List<ItemComponent> containers = new List<ItemComponent>(8);

    public static int OrdinalOf<T>(List<ItemComponent> components, ItemComponent component) where T : class {
        int ordinal = 0;
        for (int i = 0; i < components.Count; i++) {
            ItemComponent candidate = components[i];
            if (ReferenceEquals(candidate, component)) { return candidate is T ? ordinal : -1; }
            if (candidate is T) { ordinal++; }
        }
        return -1;
    }

    public static T At<T>(List<ItemComponent> components, int ordinal) where T : class {
        for (int i = 0; i < components.Count; i++) {
            if (components[i] is not T typed) { continue; }
            if (ordinal == 0) { return typed; }
            ordinal--;
        }
        return null;
    }

    public static List<ItemComponent> Containers(Item item) {
        containers.Clear();
        List<ItemComponent> components = item.Components;
        for (int i = 0; i < components.Count; i++) {
            if (components[i] is ItemContainer) { containers.Add(components[i]); }
        }
        return containers;
    }

    public static int CircuitBoxOrdinal(Item item, ItemComponent component) => OrdinalOf<CircuitBox>(item.Components, component);

    public static bool FindCircuitBox(ushort itemId, byte componentIndex, ref Option<CircuitBox> __result) {
        CircuitBox box = CircuitBox.IsRoundRunning() && Entity.FindEntityByID(itemId) is Item item
            ? At<CircuitBox>(item.Components, componentIndex)
            : null;
        __result = box != null ? Option.Some(box) : Option.None;
        return false;
    }

    public static IEnumerable<CodeInstruction> SwapContainerList(IEnumerable<CodeInstruction> instructions) =>
        IlSwap.Field(instructions, "Barotrauma.Item", "components", Method(nameof(Containers)));

    public static IEnumerable<CodeInstruction> SwapCircuitBoxIndex(IEnumerable<CodeInstruction> instructions) =>
        IlSwap.Call(instructions, "Barotrauma.Item", nameof(Item.GetComponentIndex), Method(nameof(CircuitBoxOrdinal)));

    public static void Clear() => containers.Clear();

    private static MethodInfo Method(string name) => AccessTools.Method(typeof(TypedOrdinal), name);
}
