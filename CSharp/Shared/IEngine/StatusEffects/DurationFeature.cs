using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using IEngine.Execution;
using Microsoft.Xna.Framework;
using Label = System.Reflection.Emit.Label;

namespace IEngine.StatusEffects;

sealed class DurationFeature : Feature
{
    static DurationFeature instance;
    static readonly DurationIndex index = new(StatusEffect.DurationList);
    static MethodInfo singleApply, manyApply, innerApply, singlePredicate, manyPredicate, firstTarget, equalTargets;
    static Func<StatusEffect, ISerializableEntity, Predicate<DurationListElement>> singleReference;
    static FieldInfo capturedTarget, capturedThis;
    static readonly HashSet<MethodBase> rewritten = new();
    static readonly Dictionary<MethodBase, string> failures = new();
    static string observers;
    static bool on, verify;
    static long queries, verified, mismatches;

    public override string Name => "durations";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        on = false;
        Reset();
        singleApply = AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.Apply), new[] { typeof(ActionType), typeof(float), typeof(Entity), typeof(ISerializableEntity), typeof(Vector2?) });
        manyApply = AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.Apply), new[] { typeof(ActionType), typeof(float), typeof(Entity), typeof(IReadOnlyList<ISerializableEntity>), typeof(Vector2?) });
        innerApply = AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.Apply), new[] { typeof(float), typeof(Entity), typeof(IReadOnlyList<ISerializableEntity>), typeof(Vector2?) });
        if (singleApply == null || manyApply == null || innerApply == null) { return "StatusEffect.Apply overloads are unavailable"; }
        var single = PatchProcessor.GetOriginalInstructions(singleApply);
        int singleSite = FindSite(single);
        singlePredicate = (MethodInfo)single[singleSite + 2].operand;
        var many = PatchProcessor.GetOriginalInstructions(manyApply);
        manyPredicate = (MethodInfo)many[FindSite(many) + 2].operand;
        ValidateSingleContext(single);
        ValidatePredicate(singlePredicate, true);
        ValidatePredicate(manyPredicate, false);
        singleReference = CreateSingleReference((ConstructorInfo)single[0].operand);
        rewritten.Clear();
        failures.Clear();
        foreach (MethodInfo method in new[] { singleApply, manyApply, innerApply })
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(DurationFeature), nameof(Transpile)) { priority = Priority.Last });
        }
        harmony.Patch(AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.StopAll)), postfix: new HarmonyMethod(typeof(DurationFeature), nameof(Reset)));
        return rewritten.Count == 3 ? null : FailureReport();
    }

    static int FindSite(List<CodeInstruction> code)
    {
        MethodInfo find = AccessTools.Method(typeof(List<DurationListElement>), nameof(List<DurationListElement>.Find));
        int start = -1;
        for (int i = 4; i < code.Count; i++)
        {
            if (!code[i].Calls(find)) { continue; }
            if (start >= 0) { throw new InvalidOperationException("Multiple duration searches"); }
            start = i - 4;
        }
        if (start < 0) { throw new InvalidOperationException("Duration search missing"); }
        InstructionRange.RequireShape(code, start, "ldsfld " + InstructionRange.Shape(code[start + 1]) + " ldftn newobj callvirt");
        if (!code[start].LoadsField(AccessTools.Field(typeof(StatusEffect), nameof(StatusEffect.DurationList))) ||
            code[start + 3].operand is not ConstructorInfo constructor || constructor.DeclaringType != typeof(Predicate<DurationListElement>))
        {
            throw new InvalidOperationException("Duration search operands changed");
        }
        return start;
    }

    static void ValidateSingleContext(List<CodeInstruction> code)
    {
        InstructionRange.RequireShape(code, 0, "newobj stloc ldloc ldarg.0 stfld ldloc ldarg stfld");
        capturedThis = code[4].operand as FieldInfo;
        capturedTarget = code[7].operand as FieldInfo;
        if (code[0].operand is not ConstructorInfo constructor || constructor.GetParameters().Length != 0 ||
            capturedThis?.FieldType != typeof(StatusEffect) || capturedTarget?.FieldType != typeof(ISerializableEntity) ||
            capturedThis.DeclaringType != constructor.DeclaringType || capturedTarget.DeclaringType != constructor.DeclaringType ||
            Convert.ToInt32(code[6].operand) != 4 || InstructionRange.Local(code[1]) != InstructionRange.Local(code[2]) ||
            InstructionRange.Local(code[1]) != InstructionRange.Local(code[5]))
        {
            throw new InvalidOperationException("Single-target closure contract changed");
        }
    }

    static void ValidatePredicate(MethodInfo method, bool single)
    {
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(method);
        InstructionRange.RequireShape(code, 0, single
            ? "ldarg.1 ldfld ldarg.0 ldfld bne.un ldarg.1 ldfld call ldarg.0 ldfld ceq ret ldc.i4.0 ret"
            : "ldarg.1 ldfld ldarg.0 bne.un ldarg.1 ldfld ldarg.0 ldfld call ret ldc.i4.0 ret");
        int call = single ? 7 : 8, branch = single ? 4 : 3;
        MethodInfo operation = code[call].operand as MethodInfo;
        if (code.Count != (single ? 14 : 12) || !code[1].LoadsField(AccessTools.Field(typeof(DurationListElement), nameof(DurationListElement.Parent))) ||
            !code[single ? 6 : 5].LoadsField(AccessTools.Field(typeof(DurationListElement), nameof(DurationListElement.Targets))) ||
            !code[code.Count - 2].labels.Contains((Label)code[branch].operand) ||
            operation?.DeclaringType != typeof(Enumerable) || operation.Name != (single ? nameof(Enumerable.FirstOrDefault) : nameof(Enumerable.SequenceEqual)) ||
            !operation.IsGenericMethod || operation.GetGenericArguments()[0] != typeof(ISerializableEntity) || operation.GetParameters().Length != (single ? 1 : 2) ||
            (single && (!code[3].LoadsField(capturedThis) || !code[9].LoadsField(capturedTarget))) ||
            (!single && !code[7].LoadsField(AccessTools.Field(typeof(StatusEffect), "currentTargets"))))
        {
            throw new InvalidOperationException("Duration matching contract changed");
        }
        if (single) { firstTarget = operation; }
        else { equalTargets = operation; }
    }

    static Func<StatusEffect, ISerializableEntity, Predicate<DurationListElement>> CreateSingleReference(ConstructorInfo constructor)
    {
        // Construct the real game predicate for mode=off and observed predicates.
        var method = new DynamicMethod("ie_duration_reference", typeof(Predicate<DurationListElement>),
            new[] { typeof(StatusEffect), typeof(ISerializableEntity) }, typeof(DurationFeature).Module, true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stfld, capturedThis);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, capturedTarget);
        il.Emit(OpCodes.Ldftn, singlePredicate);
        il.Emit(OpCodes.Newobj, typeof(Predicate<DurationListElement>).GetConstructor(new[] { typeof(object), typeof(IntPtr) }));
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<StatusEffect, ISerializableEntity, Predicate<DurationListElement>>>();
    }

    static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> original = InstructionRange.Copy(instructions), code = InstructionRange.Copy(original);
        rewritten.Remove(__originalMethod);
        try
        {
            if (__originalMethod == innerApply)
            {
                MethodInfo add = AccessTools.Method(typeof(List<DurationListElement>), nameof(List<DurationListElement>.Add));
                int sites = 0;
                foreach (CodeInstruction instruction in code)
                {
                    if (!instruction.Calls(add)) { continue; }
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(DurationFeature), nameof(Append));
                    sites++;
                }
                if (sites != 1) { throw new InvalidOperationException("Duration append contract changed"); }
            }
            else
            {
                bool single = __originalMethod == singleApply;
                int start = FindSite(code);
                if (!Equals(code[start + 2].operand, single ? singlePredicate : manyPredicate)) { throw new InvalidOperationException("Duration predicate replaced"); }
                if (single ? InstructionRange.Shape(code[start + 1]) != "ldloc" || InstructionRange.Local(code[start + 1]) != InstructionRange.Local(code[1])
                    : code[start + 1].opcode != OpCodes.Ldarg_0) { throw new InvalidOperationException("Duration predicate receiver changed"); }
                var replacement = new List<CodeInstruction> { new(OpCodes.Ldarg_0) };
                if (single) { replacement.Add(new CodeInstruction(OpCodes.Ldarg_S, (byte)4)); }
                replacement.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DurationFeature), single ? nameof(FindSingle) : nameof(FindMany))));
                InstructionRange.Replace(code, start, 5, replacement);
                if (single)
                {
                    ValidateSingleContext(code);
                    int local = InstructionRange.Local(code[1]);
                    for (int i = 8; i < code.Count; i++)
                    {
                        if (!InstructionRange.Shape(code[i]).StartsWith("ldloc", StringComparison.Ordinal) && InstructionRange.Shape(code[i]) != "stloc") { continue; }
                        if (InstructionRange.Local(code[i]) != local) { continue; }
                        if (InstructionRange.Shape(code[i]) != "ldloc" || i + 1 >= code.Count || !code[i + 1].LoadsField(capturedTarget))
                        {
                            throw new InvalidOperationException("Single-target closure escapes its scope");
                        }
                        InstructionRange.Replace(code, i, 2, new List<CodeInstruction> { new(OpCodes.Ldarg_S, (byte)4) });
                    }
                    InstructionRange.Replace(code, 0, 8, new List<CodeInstruction>());
                }
            }
            rewritten.Add(__originalMethod);
            failures.Remove(__originalMethod);
            return code;
        }
        catch (InvalidOperationException exception) { failures[__originalMethod] = exception.Message; return original; }
    }

    static DurationListElement FindSingle(StatusEffect parent, ISerializableEntity target)
    {
        if (!on) { return StatusEffect.DurationList.Find(singleReference(parent, target)); }
        queries++;
        DurationListElement result = index.FindSingle(parent, target);
        if (verify) { Check(result, StatusEffect.DurationList.Find(singleReference(parent, target))); }
        return result;
    }

    static DurationListElement FindMany(StatusEffect parent)
    {
        if (!on) { return StatusEffect.DurationList.Find(manyPredicate.CreateDelegate<Predicate<DurationListElement>>(parent)); }
        queries++;
        DurationListElement result = index.FindMany(parent, parent.currentTargets);
        if (verify) { Check(result, StatusEffect.DurationList.Find(manyPredicate.CreateDelegate<Predicate<DurationListElement>>(parent))); }
        return result;
    }

    static void Append(List<DurationListElement> list, DurationListElement element)
    {
        if (on && ReferenceEquals(list, StatusEffect.DurationList)) { index.Append(element); }
        else { list.Add(element); }
    }

    static void Check(DurationListElement result, DurationListElement reference)
    {
        verified++;
        if (!ReferenceEquals(result, reference)) { mismatches++; }
    }

    static void Reset() => index.Reset();
    internal override void OnLoadCompleted() => Refresh();
    internal override void RecheckObservers() => Refresh();
    protected override void OnToggled() => Refresh();
    internal override void Uninstall() { on = false; Reset(); }

    static void Refresh()
    {
        observers = null;
        foreach (MethodInfo method in new[] { singleApply, manyApply, innerApply })
        {
            PatchCensus.Foreign foreign = PatchCensus.Of(method);
            if (foreign.Transpilers.Length > 0) { observers = $"{method}: {foreign}"; break; }
        }
        foreach (MethodInfo method in new[] { singlePredicate, manyPredicate, firstTarget, equalTargets,
            AccessTools.Method(typeof(List<DurationListElement>), nameof(List<DurationListElement>.Find)) })
        {
            PatchCensus.Foreign foreign = PatchCensus.Of(method);
            if (foreign.Any) { observers = $"{method}: {foreign}"; break; }
        }
        on = instance.Active && rewritten.Count == 3 && observers == null;
        Reset();
    }

    public override void SetVerify(bool value) { verify = value; verified = mismatches = 0; }
    static string FailureReport() => string.Join("; ", failures.Select(pair => $"{pair.Key}: {pair.Value}"));
    public override string Report() => $"active={on} sites={rewritten.Count}/3 queries={queries} rebuilds={index.Rebuilds} appends={index.Appends} candidates={index.Candidates}" +
        (observers != null ? $" disabled: observed by {observers}" : "") + (failures.Count > 0 ? $" fallback: {FailureReport()}" : "") +
        (verify ? $" verified={verified} mismatches={mismatches}" : "");
}
