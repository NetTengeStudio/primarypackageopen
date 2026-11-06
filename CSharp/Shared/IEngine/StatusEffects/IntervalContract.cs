using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using IEngine.Execution;
using Label = System.Reflection.Emit.Label;

namespace IEngine.StatusEffects;

// Metadata tokens and compiler-generated names vary across client/server builds.
// Validate the complete operation, data flow and exception regions instead.
static class IntervalContract
{
    public static void Validate(List<CodeInstruction> code)
    {
        const string shape = "ldarg.0 ldfld ldc.r4 ble.un ldarg.1 brfalse ldarg.0 ldfld brfalse ldarg.0 ldfld ldarg.1 callvirt brfalse ldarg.0 ldfld stloc ldarg.1 stloc ldloc ldloc ldloc ldloc callvirt ldarg.2 sub callvirt ldarg.0 ldfld ldarg.1 callvirt ldc.r4 ble.un ldc.i4.1 ret ldsfld callvirt ldsfld ldarg.0 ldfld callvirt ldsfld dup brtrue pop ldsfld ldftn newobj dup stsfld call callvirt ldsfld callvirt stloc br ldloca call stloc ldarg.0 ldfld ldloc callvirt pop ldloca call brtrue leave ldloca constrained. callvirt endfinally ldc.i4.0 ret";
        if (code.Count != 74) { throw new InvalidOperationException("Interval instruction count changed"); }
        InstructionRange.RequireShape(code, 0, shape);
        FieldInfo interval = AccessTools.Field(typeof(StatusEffect), nameof(StatusEffect.Interval));
        FieldInfo timers = AccessTools.Field(typeof(StatusEffect), "intervalTimers");
        FieldInfo expired = AccessTools.Field(typeof(StatusEffect), "intervalsToRemove");
        Require(code[1].LoadsField(interval) && Equals(code[2].operand, 0f) && Equals(code[31].operand, 0f), "interval thresholds");
        foreach (int i in new[] { 7, 10, 15, 28, 39, 60 }) { Require(code[i].LoadsField(timers), "timer owner"); }
        foreach (int i in new[] { 35, 37, 52 }) { Require(code[i].LoadsField(expired), "expiry buffer"); }
        FieldInfo cache = code[41].operand as FieldInfo, singleton = code[45].operand as FieldInfo;
        MethodInfo predicate = code[46].operand as MethodInfo;
        Require(cache is { IsStatic: true, FieldType: not null } && cache.FieldType == typeof(Func<Entity, bool>) &&
            cache.DeclaringType.DeclaringType == typeof(StatusEffect) && Equals(code[49].operand, cache) &&
            singleton is { IsStatic: true } && singleton.FieldType == cache.DeclaringType && singleton.DeclaringType == cache.DeclaringType &&
            predicate != null && !predicate.IsStatic && predicate.DeclaringType == cache.DeclaringType,
            "expiry predicate receiver");
        Require(Equals(code[47].operand, typeof(Func<Entity, bool>).GetConstructor(new[] { typeof(object), typeof(IntPtr) })), "predicate construction");
        MethodInfo where = code[50].operand as MethodInfo;
        Require(where?.DeclaringType == typeof(Enumerable) && where.Name == nameof(Enumerable.Where) && where.IsGenericMethod &&
            where.GetGenericArguments().Length == 1 && where.GetGenericArguments()[0] == typeof(Entity) &&
            where.GetParameters().Length == 2 && where.GetParameters()[1].ParameterType == typeof(Func<Entity, bool>), "expiry filter");
        Type dictionary = typeof(Dictionary<Entity, float>), list = typeof(List<Entity>), cursor = typeof(List<Entity>.Enumerator);
        CheckCall(code, 12, AccessTools.Method(dictionary, "ContainsKey"));
        CheckCall(code, 23, AccessTools.PropertyGetter(dictionary, "Item"));
        CheckCall(code, 26, AccessTools.PropertySetter(dictionary, "Item"));
        CheckCall(code, 30, AccessTools.PropertyGetter(dictionary, "Item"));
        CheckCall(code, 36, AccessTools.Method(list, "Clear"));
        CheckCall(code, 40, AccessTools.PropertyGetter(dictionary, "Keys"));
        CheckCall(code, 51, AccessTools.Method(list, "AddRange"));
        CheckCall(code, 53, AccessTools.Method(list, "GetEnumerator"));
        CheckCall(code, 57, AccessTools.PropertyGetter(cursor, "Current"));
        CheckCall(code, 62, AccessTools.Method(dictionary, "Remove", new[] { typeof(Entity) }));
        CheckCall(code, 65, AccessTools.Method(cursor, "MoveNext"));
        CheckCall(code, 70, AccessTools.Method(typeof(IDisposable), "Dispose"));
        Require(Equals(code[69].operand, cursor), "enumerator disposal");
        foreach (var local in new[] { (16, 0), (18, 1), (19, 0), (20, 1), (21, 0), (22, 1), (54, 2), (56, 2), (58, 3), (61, 3), (64, 2), (68, 2) })
        {
            Require(InstructionRange.Local(code[local.Item1]) == local.Item2, "local data flow");
        }
        foreach (var branch in new[] { (3, 72), (5, 72), (8, 72), (13, 35), (32, 35), (43, 50), (55, 64), (66, 56), (67, 72) })
        {
            Require(code[branch.Item1].operand is Label label && code[branch.Item2].labels.Contains(label), "branch target");
        }
        for (int i = 0; i < code.Count; i++)
        {
            ExceptionBlockType? expected = i switch
            {
                55 => ExceptionBlockType.BeginExceptionBlock,
                68 => ExceptionBlockType.BeginFinallyBlock,
                71 => ExceptionBlockType.EndExceptionBlock,
                _ => null
            };
            Require(expected.HasValue ? code[i].blocks.Count == 1 && code[i].blocks[0].blockType == expected.Value : code[i].blocks.Count == 0,
                "exception region");
        }
    }

    static void CheckCall(List<CodeInstruction> code, int position, MethodInfo method) => Require(method != null && code[position].Calls(method), "interval method call");
    static void Require(bool valid, string detail)
    {
        if (!valid) { throw new InvalidOperationException("Interval contract changed: " + detail); }
    }
}
