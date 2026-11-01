using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace PrimaryPackage.TextMotion;

internal static class ChatTextDrawPatch {
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions) {
        var code = new List<CodeInstruction>(instructions);
        var empty = AccessTools.Method(typeof(string), nameof(string.IsNullOrEmpty), new[] { typeof(string) });
        var replacement = AccessTools.Method(typeof(ChatPresentation), nameof(ChatPresentation.IsTextEmpty));
        int matches = 0;
        for (int i = 0; i < code.Count; i++) {
            CodeInstruction instruction = code[i];
            if (!instruction.Calls(empty)) { yield return instruction; continue; }
            matches++;
            var self = new CodeInstruction(OpCodes.Ldarg_0);
            self.labels.AddRange(instruction.labels); instruction.labels.Clear();
            self.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
            yield return self;
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            instruction.opcode = OpCodes.Call; instruction.operand = replacement;
            yield return instruction;
        }
        if (matches != 1) { throw new InvalidOperationException("GUITextBlock.Draw must have exactly one native text gate; found " + matches); }
    }
}
