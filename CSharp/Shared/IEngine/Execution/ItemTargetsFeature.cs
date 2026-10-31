using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Barotrauma;
using HarmonyLib;
using IEngine.Core;
using IEngine.StatusEffects;
using Microsoft.Xna.Framework;

namespace IEngine.Execution;

sealed class ItemTargetsFeature : Feature
{
    static ItemTargetsFeature instance;
    static int itemSites, effectSites;
    static MethodInfo[] validators;
    static volatile bool batchValidation;
    static string observers;
    static string itemFallback, effectFallback;

    public override string Name => "itemtargets";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        validators = new[]
        {
            AccessTools.Method(typeof(StatusEffect), "IsValidTarget", new[] { typeof(ISerializableEntity) }),
            AccessTools.Method(typeof(StatusEffect), "IsValidTarget", new[] { typeof(Barotrauma.Items.Components.ItemComponent) }),
            AccessTools.Method(typeof(StatusEffect), "IsValidTarget", new[] { typeof(Item) }),
            AccessTools.Method(typeof(StatusEffect), "IsValidTarget", new[] { typeof(Character) })
        };
        foreach (MethodInfo validator in validators)
        {
            if (validator == null) { return "StatusEffect target validators differ in this build"; }
        }
        MethodInfo item = AccessTools.Method(typeof(Item), nameof(Item.ApplyStatusEffect));
        MethodInfo effect = AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.Apply),
            new[] { typeof(ActionType), typeof(float), typeof(Entity), typeof(IReadOnlyList<ISerializableEntity>), typeof(Vector2?) });
        var transpiler = new HarmonyMethod(typeof(ItemTargetsFeature), nameof(Transpile)) { priority = Priority.Last };
        harmony.Patch(item, transpiler: transpiler);
        harmony.Patch(effect, transpiler: transpiler);
        return itemSites == 1 && effectSites == 1 ? null : $"unexpected target loops: item={itemSites} effect={effectSites}";
    }

    internal override void OnLoadCompleted() => Refresh();
    internal override void RecheckObservers() => Refresh();
    protected override void OnToggled() => Refresh();

    static void Refresh()
    {
        observers = null;
        foreach (MethodInfo validator in validators)
        {
            PatchCensus.Foreign foreign = PatchCensus.Of(validator);
            if (foreign.Any) { observers = $"{validator}: {foreign}"; break; }
        }
        batchValidation = instance.Active && effectSites == 1 && observers == null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Append(IEnumerable<ISerializableEntity> source, List<ISerializableEntity> destination) => ListAppend.Copy(source, destination, instance.Active);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Filter(IReadOnlyList<ISerializableEntity> source, StatusEffect effect, List<ISerializableEntity> destination) => EffectTargets.Filter(source, effect, destination, instance.Active, batchValidation);

    static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = new List<CodeInstruction>(instructions);
        bool item = __originalMethod.DeclaringType == typeof(Item);
        try
        {
            IEnumerable<CodeInstruction> rewritten = Rewrite(code, item);
            if (item) { itemFallback = null; }
            else { effectFallback = null; }
            return rewritten;
        }
        catch (InvalidOperationException e)
        {
            if (item) { itemSites = 0; itemFallback = e.Message; }
            else { effectSites = 0; effectFallback = e.Message; }
            return code;
        }
    }

    static IEnumerable<CodeInstruction> Rewrite(List<CodeInstruction> code, bool item)
    {
        MethodInfo open = AccessTools.Method(typeof(IEnumerable<ISerializableEntity>), nameof(IEnumerable<ISerializableEntity>.GetEnumerator));
        int start = -1;
        for (int i = 0; i < code.Count; i++)
        {
            if (!code[i].Calls(open)) { continue; }
            if (start >= 0) { throw new InvalidOperationException("Multiple entity target loops"); }
            start = i;
        }
        int length = item ? 19 : 23;
        if (start < 0 || start + length >= code.Count) { throw new InvalidOperationException("Missing entity target loop"); }
        FieldInfo destination = AccessTools.Field(item ? typeof(Item) : typeof(StatusEffect), item ? "targets" : "currentTargets");
        Validate(code, start, length, destination, item);
        var replacement = new List<CodeInstruction>
        {
            new(OpCodes.Ldarg_0) { labels = code[start].labels }
        };
        if (!item) { replacement.Add(new CodeInstruction(OpCodes.Ldarg_0)); }
        replacement.Add(new CodeInstruction(OpCodes.Ldfld, destination));
        replacement.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ItemTargetsFeature), item ? nameof(Append) : nameof(Filter))));
        code.RemoveRange(start, length);
        code.InsertRange(start, replacement);
        if (item) { itemSites = 1; }
        else { effectSites = 1; }
        return code;
    }

    static void Validate(List<CodeInstruction> code, int start, int length, FieldInfo destination, bool item)
    {
        string shape = item
            ? "callvirt stloc br ldloc callvirt stloc ldarg.0 ldfld ldloc callvirt ldloc callvirt brtrue leave ldloc brfalse ldloc callvirt endfinally"
            : "callvirt stloc br ldloc callvirt stloc ldarg.0 ldloc call brfalse ldarg.0 ldfld ldloc callvirt ldloc callvirt brtrue leave ldloc brfalse ldloc callvirt endfinally";
        string[] expected = shape.Split(' ');
        for (int i = 0; i < length; i++)
        {
            if (Shape(code[start + i]) != expected[i]) { throw new InvalidOperationException("Target loop instruction contract changed"); }
        }
        int enumerator = Local(code[start + 1]), targetLocal = Local(code[start + 5]);
        int condition = item ? 10 : 14;
        for (int i = 0; i < code.Count; i++)
        {
            if (Shape(code[i]) != "ldloc") { continue; }
            int local = Local(code[i]);
            if (i < start || i >= start + length)
            {
                if (local == enumerator || local == targetLocal) { throw new InvalidOperationException("Target loop local escapes its scope"); }
                continue;
            }
            int offset = i - start;
            bool cursor = offset == 3 || offset == condition || offset == condition + 4 || offset == condition + 6;
            if (local != (cursor ? enumerator : targetLocal)) { throw new InvalidOperationException("Target loop data flow changed"); }
        }
        if (!code[start + condition].labels.Contains((System.Reflection.Emit.Label)code[start + 2].operand) ||
            !code[start + 3].labels.Contains((System.Reflection.Emit.Label)code[start + condition + 2].operand) ||
            !code[start + length - 1].labels.Contains((System.Reflection.Emit.Label)code[start + condition + 5].operand) ||
            !code[start + length].labels.Contains((System.Reflection.Emit.Label)code[start + condition + 3].operand) ||
            (!item && !code[start + condition].labels.Contains((System.Reflection.Emit.Label)code[start + 9].operand)))
        {
            throw new InvalidOperationException("Target loop control flow changed");
        }
        MethodInfo current = AccessTools.PropertyGetter(typeof(IEnumerator<ISerializableEntity>), nameof(IEnumerator<ISerializableEntity>.Current));
        MethodInfo next = AccessTools.Method(typeof(IEnumerator), nameof(IEnumerator.MoveNext));
        MethodInfo dispose = AccessTools.Method(typeof(IDisposable), nameof(IDisposable.Dispose));
        MethodInfo add = AccessTools.Method(typeof(List<ISerializableEntity>), nameof(List<ISerializableEntity>.Add));
        MethodInfo valid = AccessTools.Method(typeof(StatusEffect), "IsValidTarget", new[] { typeof(ISerializableEntity) });
        int currents = 0, moves = 0, disposes = 0, adds = 0, validations = 0, fields = 0, blocks = 0;
        var internalLabels = new HashSet<System.Reflection.Emit.Label>();
        for (int i = start + 1; i < start + length; i++) { internalLabels.UnionWith(code[i].labels); }
        for (int i = 0; i < code.Count; i++)
        {
            CodeInstruction instruction = code[i];
            bool inside = i >= start && i < start + length;
            if (!inside)
            {
                if (instruction.operand is System.Reflection.Emit.Label target && internalLabels.Contains(target)) { throw new InvalidOperationException("External branch enters target loop"); }
                if (instruction.operand is System.Reflection.Emit.Label[] targets)
                {
                    foreach (System.Reflection.Emit.Label label in targets)
                    {
                        if (internalLabels.Contains(label)) { throw new InvalidOperationException("External switch enters target loop"); }
                    }
                }
                continue;
            }
            foreach (ExceptionBlock block in instruction.blocks)
            {
                if ((blocks == 0 && block.blockType != ExceptionBlockType.BeginExceptionBlock) ||
                    (blocks == 1 && block.blockType != ExceptionBlockType.BeginFinallyBlock) ||
                    (blocks == 2 && block.blockType != ExceptionBlockType.EndExceptionBlock) || blocks > 2)
                {
                    throw new InvalidOperationException("Target loop exception region changed");
                }
                blocks++;
            }
            if (instruction.LoadsField(destination)) { fields++; }
            if (i == start || (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt)) { continue; }
            if (instruction.Calls(current)) { currents++; }
            else if (instruction.Calls(next)) { moves++; }
            else if (instruction.Calls(dispose)) { disposes++; }
            else if (instruction.Calls(add)) { adds++; }
            else if (instruction.Calls(valid)) { validations++; }
            else { throw new InvalidOperationException("Unknown computation in target loop"); }
        }
        if (code[start + length - 1].opcode != OpCodes.Endfinally || currents != 1 || moves != 1 || disposes != 1 || adds != 1 ||
            fields != 1 || validations != (item ? 0 : 1) || blocks != 3)
        {
            throw new InvalidOperationException("Target loop contract changed");
        }
    }

    static string Shape(CodeInstruction instruction)
    {
        string name = instruction.opcode.Name;
        if (name.StartsWith("ldloc", StringComparison.Ordinal)) { return "ldloc"; }
        if (name.StartsWith("stloc", StringComparison.Ordinal)) { return "stloc"; }
        return name.EndsWith(".s", StringComparison.Ordinal) ? name.Substring(0, name.Length - 2) : name;
    }

    static int Local(CodeInstruction instruction)
    {
        OpCode op = instruction.opcode;
        if (op == OpCodes.Ldloc_0 || op == OpCodes.Stloc_0) { return 0; }
        if (op == OpCodes.Ldloc_1 || op == OpCodes.Stloc_1) { return 1; }
        if (op == OpCodes.Ldloc_2 || op == OpCodes.Stloc_2) { return 2; }
        if (op == OpCodes.Ldloc_3 || op == OpCodes.Stloc_3) { return 3; }
        return instruction.operand is LocalBuilder local ? local.LocalIndex : Convert.ToInt32(instruction.operand);
    }

    internal override void Uninstall() => batchValidation = false;

    public override string Report() => $"itemLoops={itemSites} effectLoops={effectSites} bulkValidation={batchValidation}" +
        (observers != null ? $" validation observed: {observers}" : "") +
        (itemFallback != null ? $" item fallback: {itemFallback}" : "") +
        (effectFallback != null ? $" effect fallback: {effectFallback}" : "");
}
