using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;

namespace INet;

/// <summary>A removal of an already absent entity is a valid spawner history entry.
/// Preserve that entry in the native error report instead of throwing while diagnosing the original desync.</summary>
internal static class EventErrorDiagnostics
{
    public static void Attach(Harmony harmony, PrimaryPackage.Core.PatchSet patches) => patches.Patch(harmony,
        AccessTools.Method(typeof(GameClient), "WriteEventErrorData", new[] { typeof(ClientNetError), typeof(ushort), typeof(ushort), typeof(ushort) }),
        transpiler: new HarmonyMethod(typeof(EventErrorDiagnostics), nameof(NullHistoryEntries)));

    private static string Describe(Entity entity) => entity?.ToString() ?? "<entity already absent>";
    private static string IdText(Entity entity) => entity == null ? "unknown" : entity.ID.ToString();

    private static IEnumerable<CodeInstruction> NullHistoryEntries(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        bool history = false;
        int descriptions = 0, ids = 0;
        for (int i = 0; i < code.Count; i++)
        {
            CodeInstruction instruction = code[i];
            if (instruction.operand is FieldInfo field && field.DeclaringType == typeof(EntitySpawner) && field.Name == "receivedEvents") { history = true; }
            if (history && ids == 0 && instruction.opcode == OpCodes.Ldflda && instruction.operand is FieldInfo id &&
                id.DeclaringType == typeof(Entity) && id.Name == nameof(Entity.ID) && i + 1 < code.Count &&
                code[i + 1].operand is MethodInfo format && format.DeclaringType == typeof(ushort) && format.Name == nameof(ToString) && format.GetParameters().Length == 0)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(EventErrorDiagnostics), nameof(IdText));
                code[i + 1].opcode = OpCodes.Nop; code[i + 1].operand = null;
                ids++;
            }
            if (!history || instruction.operand is not MethodInfo method) { continue; }
            if (method == AccessTools.Method(typeof(object), nameof(ToString), Type.EmptyTypes) && descriptions == 0)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(EventErrorDiagnostics), nameof(Describe));
                descriptions++;
            }
        }
        if (descriptions != 1 || ids != 1) { throw new InvalidOperationException("Native entity-error history has changed"); }
        return code;
    }
}
