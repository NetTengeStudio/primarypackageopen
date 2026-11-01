using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PrimaryPackage.Core;


public sealed class ConsolePresentationPlugin : IModule {
    public void Attach(Harmony harmony) {
        MethodInfo target = AccessTools.Method(typeof(DebugConsole), nameof(DebugConsole.ThrowError),
            new[] { typeof(string), typeof(Exception), typeof(ContentPackage), typeof(bool), typeof(bool) });
        if (target == null) { throw new MissingMethodException("DebugConsole.ThrowError"); }
        harmony.Patch(target, transpiler: ModuleHost.Bind(typeof(ConsolePresentationPlugin), nameof(Transpile)));
    }

    public void RoundStart() { }
    public void Release() { }

    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions) {
        var code = new List<CodeInstruction>(instructions);
        FieldInfo open = AccessTools.Field(typeof(DebugConsole), "isOpen");
        int replaced = 0;
        for (int i = 0; i < code.Count; i++) {
            CodeInstruction instruction = code[i];
            if (instruction.opcode != OpCodes.Stsfld || !Equals(instruction.operand, open)) { continue; }
            
            
            instruction.opcode = OpCodes.Pop;
            instruction.operand = null;
            replaced++;
        }
        if (replaced != 1) { throw new InvalidOperationException("Expected one console auto-open in ThrowError, found " + replaced); }
        return code;
    }
}
