using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using IEngine.Execution;
using Label = System.Reflection.Emit.Label;

namespace IEngine.Power;

sealed class PowerFeature : Feature
{
    static PowerFeature instance;
    static readonly Stack<PowerSourceGroup> groups = new();
    static int viewSites, clearSites, newSites, voltageSites;
    static MethodInfo voltageGetter, gridPredicate, voltageSelector;
    static volatile bool voltageOn;
    static string voltageObservers, voltageFallback;
    public static long Rented, Created;

    public override string Name => "power";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        MethodInfo update = AccessTools.Method(typeof(Powered), nameof(Powered.UpdatePower));
        MethodInfo addSrc = AccessTools.Method(typeof(GridInfo), nameof(GridInfo.AddSrc));
        harmony.Patch(update, transpiler: new HarmonyMethod(typeof(PowerFeature), nameof(UpdateTranspiler)));
        harmony.Patch(addSrc, transpiler: new HarmonyMethod(typeof(PowerFeature), nameof(AddSrcTranspiler)));
        voltageGetter = AccessTools.PropertyGetter(typeof(Powered), nameof(Powered.Voltage));
        harmony.Patch(voltageGetter, transpiler: new HarmonyMethod(typeof(PowerFeature), nameof(VoltageTranspiler)) { priority = Priority.Last });
        return viewSites == 3 && clearSites == 1 && newSites == 1 ? null : $"unexpected IL shape: views={viewSites} clears={clearSites} news={newSites}";
    }

    internal override void OnLoadCompleted() => RefreshVoltage();
    internal override void RecheckObservers() => RefreshVoltage();
    protected override void OnToggled() => RefreshVoltage();

    static void RefreshVoltage()
    {
        PatchCensus.Foreign foreign = PatchCensus.Of(voltageGetter);
        voltageObservers = foreign.Transpilers.Length == 0 ? null : foreign.ToString();
        foreach (MethodInfo projection in new[] { gridPredicate, voltageSelector })
        {
            if (projection == null) { continue; }
            PatchCensus.Foreign observed = PatchCensus.Of(projection);
            if (observed.Any) { voltageObservers = $"{projection.Name}: {observed}"; break; }
        }
        voltageOn = instance.Active && voltageSites == 1 && voltageObservers == null;
    }

    static bool ReduceVoltage() => voltageOn;

    static IEnumerable<CodeInstruction> VoltageTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = new List<CodeInstruction>(instructions);
        try
        {
            IEnumerable<CodeInstruction> rewritten = RewriteVoltage(code, generator);
            voltageFallback = null;
            return rewritten;
        }
        catch (InvalidOperationException e)
        {
            voltageSites = 0;
            voltageFallback = e.Message;
            return code;
        }
    }

    static IEnumerable<CodeInstruction> RewriteVoltage(List<CodeInstruction> code, ILGenerator generator)
    {
        FieldInfo outputs = AccessTools.Field(typeof(Powered), "powerOuts");
        int start = -1;
        for (int i = 1; i + 2 < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Ldfld || !Equals(code[i].operand, outputs) ||
                code[i + 1].operand is not MethodInfo any || any.DeclaringType != typeof(System.Linq.Enumerable) || any.Name != "Any") { continue; }
            if (start >= 0 || code[i - 1].opcode != OpCodes.Ldarg_0 ||
                (code[i + 2].opcode != OpCodes.Brfalse && code[i + 2].opcode != OpCodes.Brfalse_S))
            {
                throw new InvalidOperationException("Unexpected voltage reduction branch");
            }
            start = i - 1;
        }
        if (start < 0) { throw new InvalidOperationException("Missing voltage reduction branch"); }
        ValidateVoltage(code, start, outputs);
        Label fallback = (Label)code[start + 3].operand;
        Label original = generator.DefineLabel();
        LocalBuilder average = generator.DeclareLocal(typeof(float));
        var injected = new List<CodeInstruction>
        {
            new(OpCodes.Call, AccessTools.Method(typeof(PowerFeature), nameof(ReduceVoltage))) { labels = code[start].labels },
            new(OpCodes.Brfalse, original),
            new(OpCodes.Ldarg_0),
            new(OpCodes.Ldfld, outputs),
            new(OpCodes.Ldloca, average),
            new(OpCodes.Call, AccessTools.Method(typeof(VoltageReduction), nameof(VoltageReduction.TryAverage))),
            new(OpCodes.Brfalse, fallback),
            new(OpCodes.Ldloc, average),
            new(OpCodes.Ret)
        };
        code[start].labels = new List<Label> { original };
        code.InsertRange(start, injected);
        voltageSites = 1;
        return code;
    }

    static void ValidateVoltage(List<CodeInstruction> code, int start, FieldInfo outputs)
    {
        string[] shape = "ldarg.0 ldfld call brfalse ldarg.0 ldfld ldsfld dup brtrue pop ldsfld ldftn newobj dup stsfld call stloc ldloc call brfalse ldloc ldsfld dup brtrue pop ldsfld ldftn newobj dup stsfld call ret".Split(' ');
        if (start + shape.Length > code.Count) { throw new InvalidOperationException("Incomplete voltage reduction"); }
        for (int i = 0; i < shape.Length; i++)
        {
            string name = code[start + i].opcode.Name;
            if (name.StartsWith("ldloc", StringComparison.Ordinal)) { name = "ldloc"; }
            else if (name.StartsWith("stloc", StringComparison.Ordinal)) { name = "stloc"; }
            else if (name.EndsWith(".s", StringComparison.Ordinal)) { name = name.Substring(0, name.Length - 2); }
            if (name != shape[i]) { throw new InvalidOperationException("Voltage instruction contract changed"); }
        }
        int any = 0, where = 0, average = 0, fields = 0;
        var projections = new List<MethodInfo>(2);
        object fallback = code[start + 3].operand;
        List<Label> fallbackLabels = code.Find(instruction => instruction.labels.Contains((Label)fallback))?.labels;
        if (fallbackLabels == null) { throw new InvalidOperationException("Missing voltage fallback"); }
        for (int i = start; i < code.Count; i++)
        {
            CodeInstruction instruction = code[i];
            if (instruction.opcode == OpCodes.Ret) { break; }
            if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, outputs)) { fields++; }
            if (instruction.opcode == OpCodes.Ldftn && instruction.operand is MethodInfo projection) { projections.Add(projection); }
            if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt) { continue; }
            if (instruction.operand is not MethodInfo call || call.DeclaringType != typeof(System.Linq.Enumerable))
            {
                throw new InvalidOperationException("Unknown operation in voltage reduction");
            }
            if (call.Name == "Any" && call.GetParameters().Length == 1 && code[i + 1].operand is Label target && fallbackLabels.Contains(target)) { any++; }
            else if (call.Name == "Where") { where++; }
            else if (call.Name == "Average" && call.ReturnType == typeof(float)) { average++; }
            else { throw new InvalidOperationException("Unknown LINQ operation in voltage reduction"); }
        }
        if (any != 2 || where != 1 || average != 1 || fields != 2 || projections.Count != 2)
        {
            throw new InvalidOperationException("Voltage reduction contract changed");
        }
        FieldInfo grid = AccessTools.Field(typeof(Connection), nameof(Connection.Grid));
        FieldInfo voltage = AccessTools.Field(typeof(GridInfo), nameof(GridInfo.Voltage));
        List<CodeInstruction> predicate = PatchProcessor.GetOriginalInstructions(projections[0]);
        List<CodeInstruction> selector = PatchProcessor.GetOriginalInstructions(projections[1]);
        if (predicate.Count != 5 || predicate[0].opcode != (projections[0].IsStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1) || !predicate[1].LoadsField(grid) ||
            predicate[2].opcode != OpCodes.Ldnull || predicate[3].opcode != OpCodes.Cgt_Un || predicate[4].opcode != OpCodes.Ret ||
            selector.Count != 4 || selector[0].opcode != (projections[1].IsStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1) || !selector[1].LoadsField(grid) ||
            !selector[2].LoadsField(voltage) || selector[3].opcode != OpCodes.Ret)
        {
            throw new InvalidOperationException("Voltage projection contract changed");
        }
        gridPredicate = projections[0];
        voltageSelector = projections[1];
    }

    static IEnumerable<CodeInstruction> UpdateTranspiler(IEnumerable<CodeInstruction> code)
    {
        MethodInfo getComponents = AccessTools.Method(typeof(Item), nameof(Item.GetComponents)).MakeGenericMethod(typeof(Powered));
        MethodInfo clear = AccessTools.Method(typeof(SortedList<PowerPriority, PowerSourceGroup>), nameof(SortedList<PowerPriority, PowerSourceGroup>.Clear));
        foreach (CodeInstruction ci in code)
        {
            if (ci.Calls(getComponents))
            {
                viewSites++;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PowerFeature), nameof(Components))) { labels = ci.labels, blocks = ci.blocks };
                continue;
            }
            if (ci.Calls(clear))
            {
                clearSites++;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PowerFeature), nameof(ClearGroups))) { labels = ci.labels, blocks = ci.blocks };
                continue;
            }
            yield return ci;
        }
    }

    static IEnumerable<CodeInstruction> AddSrcTranspiler(IEnumerable<CodeInstruction> code)
    {
        ConstructorInfo ctor = AccessTools.Constructor(typeof(PowerSourceGroup));
        foreach (CodeInstruction ci in code)
        {
            if (ci.opcode == OpCodes.Newobj && ci.operand is ConstructorInfo c && c == ctor)
            {
                newSites++;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PowerFeature), nameof(RentGroup))) { labels = ci.labels, blocks = ci.blocks };
                continue;
            }
            yield return ci;
        }
    }

    static IEnumerable<Powered> Components(Item item) => instance.Active ? PoweredView.Of(item) : item.GetComponents<Powered>();

    static void ClearGroups(SortedList<PowerPriority, PowerSourceGroup> list)
    {
        if (instance.Active)
        {
            IList<PowerSourceGroup> values = list.Values;
            for (int i = 0; i < values.Count; i++) { groups.Push(values[i]); }
        }
        list.Clear();
    }

    static PowerSourceGroup RentGroup()
    {
        if (!instance.Active || groups.Count == 0)
        {
            Created++;
            return new PowerSourceGroup();
        }
        Rented++;
        PowerSourceGroup g = groups.Pop();
        g.Connections.Clear();
        g.MinMaxPower = default;
        return g;
    }

    internal override void Uninstall()
    {
        voltageOn = false;
        groups.Clear();
    }

    public override string Report() => $"views={viewSites} clears={clearSites} news={newSites} rented={Rented} created={Created} pooled={groups.Count} voltage={voltageOn} voltageSites={voltageSites}" +
        (voltageObservers != null ? $" voltage disabled: {voltageObservers}" : "") +
        (voltageFallback != null ? $" voltage fallback: {voltageFallback}" : "");
}
