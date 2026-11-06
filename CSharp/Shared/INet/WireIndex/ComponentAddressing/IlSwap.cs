using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace INet;

public static class IlSwap {
    public static IEnumerable<CodeInstruction> Field(IEnumerable<CodeInstruction> instructions, string declaringType, string fieldName, MethodInfo replacement) {
        var code = new List<CodeInstruction>(instructions);
        int swapped = 0;
        int present = 0;
        for (int i = 0; i < code.Count; i++) {
            CodeInstruction instruction = code[i];
            if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field &&
                field.Name == fieldName && field.DeclaringType?.FullName == declaringType) {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                swapped++;
            } else if (Calls(instruction, replacement)) {
                present++;
            }
        }
        if (swapped + present == 0) { throw new InvalidOperationException($"{declaringType}.{fieldName} is not read here"); }
        return code;
    }

    public static IEnumerable<CodeInstruction> Call(IEnumerable<CodeInstruction> instructions, string declaringType, string methodName, MethodInfo replacement) {
        var code = new List<CodeInstruction>(instructions);
        int swapped = 0;
        int present = 0;
        for (int i = 0; i < code.Count; i++) {
            CodeInstruction instruction = code[i];
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo method &&
                method.Name == methodName && method.DeclaringType?.FullName == declaringType) {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                swapped++;
            } else if (Calls(instruction, replacement)) {
                present++;
            }
        }
        if (swapped + present == 0) { throw new InvalidOperationException($"{declaringType}.{methodName} is not called here"); }
        return code;
    }


    public static IEnumerable<CodeInstruction> Element(IEnumerable<CodeInstruction> instructions, string elementType, MethodInfo replacement) {
        var code = new List<CodeInstruction>(instructions);
        int swapped = 0;
        int present = 0;
        for (int i = 0; i < code.Count; i++) {
            CodeInstruction instruction = code[i];
            if (instruction.opcode == OpCodes.Ldelem_Ref && PrecededByArrayOf(code, i, elementType)) {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                swapped++;
            } else if (Calls(instruction, replacement)) {
                present++;
            }
        }
        if (swapped + present == 0) { throw new InvalidOperationException($"{elementType}[] is not indexed here"); }
        return code;
    }

    public static IEnumerable<CodeInstruction> ListItem(IEnumerable<CodeInstruction> instructions, string elementType, MethodInfo replacement) {
        var code = new List<CodeInstruction>(instructions);
        int swapped = 0;
        int present = 0;
        for (int i = 0; i < code.Count; i++) {
            CodeInstruction instruction = code[i];
            if (instruction.operand is MethodInfo method && method.Name == "get_Item" &&
                method.DeclaringType is { IsGenericType: true } owner &&
                owner.GetGenericTypeDefinition().FullName == "System.Collections.Generic.List`1" &&
                owner.GetGenericArguments()[0].FullName == elementType) {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                swapped++;
            } else if (Calls(instruction, replacement)) {
                present++;
            }
        }
        if (swapped + present == 0) { throw new InvalidOperationException($"no {elementType} list is indexed here"); }
        return code;
    }

    private static bool PrecededByArrayOf(List<CodeInstruction> code, int index, string elementType) {
        for (int i = index - 1; i >= 0 && i >= index - 8; i--) {
            object operand = code[i].operand;
            if (operand is MethodInfo method && method.ReturnType.IsArray &&
                method.ReturnType.GetElementType()?.FullName == elementType) { return true; }
            if (operand is FieldInfo field && field.FieldType.IsArray &&
                field.FieldType.GetElementType()?.FullName == elementType) { return true; }
        }
        return false;
    }

    private static bool Calls(CodeInstruction instruction, MethodInfo replacement) =>
        instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method &&
        method.Name == replacement.Name && method.DeclaringType?.FullName == replacement.DeclaringType?.FullName;
}