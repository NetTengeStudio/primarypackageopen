using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Xml.Linq;
using Barotrauma;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.SubEditor;

public static class ExactIds
{
    private sealed class LoadContext(XElement root, EntityIdMap ids, LoadContext previous)
    {
        public readonly XElement Root = root;
        public readonly EntityIdMap Ids = ids;
        public readonly LoadContext Previous = previous;
        public IdRemap Remap;
    }

    [ThreadStatic] private static LoadContext current;

    public static void Attach(Harmony harmony)
    {
        ModuleHost.Guard("IdRemap.GetOffsetId", () => harmony.Patch(
            AccessTools.Method(typeof(IdRemap), nameof(IdRemap.GetOffsetId), new[] { typeof(int) }),
            prefix: ModuleHost.Bind(typeof(ExactIds), nameof(Prefix))));
        ModuleHost.Guard("IdRemap..ctor", () => harmony.Patch(
            AccessTools.Constructor(typeof(IdRemap), new[] { typeof(XElement), typeof(int) }),
            prefix: ModuleHost.Bind(typeof(ExactIds), nameof(Constructing))));
        harmony.Patch(AccessTools.Method(typeof(LinkedSubmarine), nameof(LinkedSubmarine.Load)),
            transpiler: ModuleHost.Bind(typeof(ExactIds), nameof(LinkedLoading)));
    }

    private static void Constructing(IdRemap __instance, XElement parentElement)
    {
        if (current != null && ReferenceEquals(parentElement, current.Root)) { current.Remap = __instance; }
    }

    private static IEnumerable<CodeInstruction> LinkedLoading(IEnumerable<CodeInstruction> instructions)
    {
        var native = AccessTools.Method(typeof(IdRemap), nameof(IdRemap.AssignMaxId));
        var mapped = AccessTools.Method(typeof(ExactIds), nameof(AssignLinked));
        int replaced = 0;
        var list = new List<CodeInstruction>();
        foreach (CodeInstruction instruction in instructions)
        {
            if (!instruction.Calls(native)) { list.Add(instruction); continue; }
            list.Add(new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction));
            list.Add(new CodeInstruction(OpCodes.Call, mapped));
            replaced++;
        }
        if (replaced != 1)
        {
            ModLog.Throttled("exactids_transpile", "Linked submarine ID assignment changed, skipping transpile fallback");
            return instructions;
        }
        return list;
    }

    private static void AssignLinked(IdRemap remap, out ushort result, ContentXElement element)
    {
        for (LoadContext context = current; context != null; context = context.Previous)
        {
            if (context.Remap != remap) { continue; }
            result = context.Ids.Resolve(element.GetAttributeInt("ID", 0));
            if (result == 0) { throw new InvalidOperationException("Linked submarine has no reserved ID"); }
            return;
        }
        remap.AssignMaxId(out result);
    }

    private static bool Prefix(IdRemap __instance, int id, ref ushort __result)
    {
        for (LoadContext c = current; c != null; c = c.Previous)
        {
            if (!ReferenceEquals(c.Remap, __instance)) { continue; }
            __result = c.Ids.Resolve(id);
            return false;
        }
        return true;
    }

    public static List<MapEntity> Load(Submarine submarine, XElement root, EntityIdMap ids, int first)
    {
        var context = new LoadContext(root, ids, current);
        current = context;
        try { return MapEntity.LoadAll(submarine, root, "subeditor", first); }
        finally { current = context.Previous; }
    }
}
