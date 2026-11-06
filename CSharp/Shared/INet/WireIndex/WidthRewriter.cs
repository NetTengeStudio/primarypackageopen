using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace INet;

public static class WidthRewriter {
    private const string WriteRanged = "WriteRangedInteger";
    private const string ReadRanged = "ReadRangedInteger";
    private const int Window = 12;

    public static IEnumerable<CodeInstruction> Keyed(IEnumerable<CodeInstruction> instructions, string owner, string member,
        MethodInfo write, MethodInfo read) => Apply(instructions, owner, member, write, read, true);

    public static IEnumerable<CodeInstruction> Bounded(IEnumerable<CodeInstruction> instructions, string owner, string member) =>
        Apply(instructions, owner, member,
            AccessTools.Method(typeof(WireBounds), nameof(WireBounds.Write)),
            AccessTools.Method(typeof(WireBounds), nameof(WireBounds.Read)), false);

    private static IEnumerable<CodeInstruction> Apply(IEnumerable<CodeInstruction> instructions, string owner, string member,
        MethodInfo write, MethodInfo read, bool needsEntity) {
        var code = new List<CodeInstruction>(instructions);
        int swapped = 0;
        int present = 0;
        for (int i = 0; i < code.Count; i++) {
            if (code[i].operand is not MethodInfo target) { continue; }
            if (Is(target, write) || Is(target, read)) {
                present++;
                continue;
            }
            bool isWrite = target.Name == WriteRanged;
            if (!isWrite && target.Name != ReadRanged) { continue; }
            if (!DerivesFrom(code, i, owner, member)) { continue; }
            if (needsEntity) {
                var entity = new CodeInstruction(OpCodes.Ldarg_0);
                entity.labels.AddRange(code[i].labels);
                code[i].labels.Clear();
                entity.blocks.AddRange(code[i].blocks);
                code[i].blocks.Clear();
                code.Insert(i, entity);
                i++;
            }
            code[i].opcode = OpCodes.Call;
            code[i].operand = isWrite ? write : read;
            swapped++;
        }
        if (swapped + present == 0) { throw new InvalidOperationException($"{owner}.{member} does not size a wire field here"); }
        return code;
    }

    private static bool DerivesFrom(List<CodeInstruction> code, int callIndex, string owner, string member) {
        for (int i = callIndex - 1; i >= 0 && i >= callIndex - Window; i--) {
            object operand = code[i].operand;
            if (operand is FieldInfo field && field.Name == member && field.DeclaringType?.FullName == owner) { return true; }
            if (operand is MethodInfo method && method.Name == "get_" + member && method.DeclaringType?.FullName == owner) { return true; }
        }
        return false;
    }

    private static bool Is(MethodInfo target, MethodInfo bridge) =>
        target.Name == bridge.Name && target.DeclaringType?.FullName == bridge.DeclaringType?.FullName;
}
