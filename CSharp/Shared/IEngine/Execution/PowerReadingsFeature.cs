using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace IEngine.Execution;

sealed class PowerReadingsFeature : Feature
{
    static PowerReadingsFeature instance;
    static readonly MethodInfo readings = AccessTools.Method(typeof(PowerTransfer), "SendSignals");
    static int readingSites;
    static bool on;
    public override string Name => "powerreadings";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        if (!NativeCode.Verified) { return "Native module identity changed"; }
        harmony.Patch(readings, transpiler: new HarmonyMethod(typeof(PowerReadingsFeature), nameof(ReadingsTranspiler)) { priority = Priority.Last });
        return readingSites == 2 ? null : "Unexpected power reading comparisons: " + readingSites;
    }

    static int SignalReading(float value) => on ? (int)Math.Round(value) : (int)value;

    static IEnumerable<CodeInstruction> ReadingsTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        int count = 0;
        for (int i = 1; i < code.Count; i++)
        {
            if (code[i].opcode == OpCodes.Conv_I4 && code[i - 1].opcode.Name.StartsWith("ldloc", StringComparison.Ordinal)) { count++; }
        }
        readingSites = count;
        if (count != 2) { return code; }
        MethodInfo reading = AccessTools.Method(typeof(PowerReadingsFeature), nameof(SignalReading));
        for (int i = 1; i < code.Count; i++)
        {
            CodeInstruction instruction = code[i];
            if (instruction.opcode == OpCodes.Conv_I4 && code[i - 1].opcode.Name.StartsWith("ldloc", StringComparison.Ordinal))
            {
                code[i] = new CodeInstruction(OpCodes.Call, reading) { labels = instruction.labels, blocks = instruction.blocks };
            }
        }
        return code;
    }

    static void Refresh() => on = instance is { Active: true } && !PatchCensus.Of(readings).Any;
    internal override void OnLoadCompleted() => Refresh();
    internal override void RecheckObservers() => Refresh();
    protected override void OnToggled() => Refresh();
    internal override void Uninstall() => on = false;
    public override string Report() => $"active={on} comparisons={readingSites}";
}
