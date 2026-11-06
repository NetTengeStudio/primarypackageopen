using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Label = System.Reflection.Emit.Label;

namespace IEngine.Execution;

static class InstructionRange
{
    public static List<CodeInstruction> Copy(IEnumerable<CodeInstruction> instructions)
    {
        var copy = new List<CodeInstruction>();
        foreach (CodeInstruction instruction in instructions)
        {
            copy.Add(new CodeInstruction(instruction.opcode, instruction.operand)
            {
                labels = new List<Label>(instruction.labels),
                blocks = new List<ExceptionBlock>(instruction.blocks)
            });
        }
        return copy;
    }

    public static string Shape(CodeInstruction instruction)
    {
        string name = instruction.opcode.Name;
        if (name.StartsWith("ldloca", StringComparison.Ordinal)) { return "ldloca"; }
        if (name.StartsWith("ldloc", StringComparison.Ordinal)) { return "ldloc"; }
        if (name.StartsWith("stloc", StringComparison.Ordinal)) { return "stloc"; }
        if (name.StartsWith("ldarg.", StringComparison.Ordinal) && name is not ("ldarg.0" or "ldarg.1" or "ldarg.2" or "ldarg.3")) { return "ldarg"; }
        return name.EndsWith(".s", StringComparison.Ordinal) ? name.Substring(0, name.Length - 2) : name;
    }

    public static void RequireShape(List<CodeInstruction> code, int start, string shape)
    {
        string[] expected = shape.Split(' ');
        if (start < 0 || start + expected.Length > code.Count) { throw new InvalidOperationException("Incomplete instruction range"); }
        for (int i = 0; i < expected.Length; i++)
        {
            if (Shape(code[start + i]) != expected[i]) { throw new InvalidOperationException($"Instruction contract changed at {start + i}"); }
        }
    }

    public static int Local(CodeInstruction instruction)
    {
        OpCode op = instruction.opcode;
        if (op == OpCodes.Ldloc_0 || op == OpCodes.Stloc_0) { return 0; }
        if (op == OpCodes.Ldloc_1 || op == OpCodes.Stloc_1) { return 1; }
        if (op == OpCodes.Ldloc_2 || op == OpCodes.Stloc_2) { return 2; }
        if (op == OpCodes.Ldloc_3 || op == OpCodes.Stloc_3) { return 3; }
        return instruction.operand switch
        {
            LocalBuilder local => local.LocalIndex,
            LocalVariableInfo local => local.LocalIndex,
            byte index => index,
            int index => index,
            _ => throw new InvalidOperationException("Unknown local operand")
        };
    }

    public static void Replace(List<CodeInstruction> code, int start, int count, List<CodeInstruction> replacement)
    {
        var labels = new HashSet<Label>();
        for (int i = start; i < start + count; i++)
        {
            if (code[i].blocks.Count != 0) { throw new InvalidOperationException("Exception region intersects replacement"); }
            if (i > start) { labels.UnionWith(code[i].labels); }
        }
        // Straight-line ranges only: even an internal branch changes the contract.
        foreach (CodeInstruction instruction in code)
        {
            if (instruction.operand is Label target && labels.Contains(target)) { throw new InvalidOperationException("Branch enters replacement range"); }
            if (instruction.operand is Label[] targets)
            {
                foreach (Label label in targets)
                {
                    if (labels.Contains(label)) { throw new InvalidOperationException("Switch enters replacement range"); }
                }
            }
        }
        if (replacement.Count == 0)
        {
            if (start + count == code.Count) { throw new InvalidOperationException("Cannot remove method tail"); }
            code[start + count].labels.AddRange(code[start].labels);
        }
        else { replacement[0].labels.AddRange(code[start].labels); }
        code.RemoveRange(start, count);
        code.InsertRange(start, replacement);
    }
}
