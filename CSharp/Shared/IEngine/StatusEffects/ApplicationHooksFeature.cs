using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Compatibility;
using HarmonyLib;
using IEngine.Execution;
using Microsoft.Xna.Framework;

namespace IEngine.StatusEffects;

sealed class ApplicationHooksFeature : Feature
{
    static ApplicationHooksFeature instance;
    static MethodInfo apply, hookCall, eventCall;
    static volatile bool on;
    static int sites;
    static string fallback, observers;

    public override string Name => "effecthooks";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        on = false;
        ApplicationHooks.Reset();
        apply = AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.Apply), new[] { typeof(float), typeof(Entity), typeof(IReadOnlyList<ISerializableEntity>), typeof(Vector2?) });
        foreach (MethodInfo method in typeof(ILuaCsHook).GetMethods())
        {
            if (method.Name == nameof(ILuaCsHook.Call) && method.IsGenericMethodDefinition) { hookCall = method.MakeGenericMethod(typeof(bool?)); }
        }
        foreach (MethodInfo method in typeof(EventService).GetMethods())
        {
            if (method.Name == nameof(EventService.Call) && method.IsGenericMethodDefinition) { eventCall = method.MakeGenericMethod(typeof(bool?)); }
        }
        if (apply == null || hookCall == null || eventCall == null) { return "StatusEffect/LuaCs hook methods unavailable"; }
        harmony.Patch(apply, transpiler: new HarmonyMethod(typeof(ApplicationHooksFeature), nameof(Transpile)) { priority = Priority.Last });
        var reset = new HarmonyMethod(typeof(ApplicationHooks), nameof(ApplicationHooks.Reset));
        harmony.Patch(AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.StopAll)), postfix: reset);
        harmony.Patch(AccessTools.Method(typeof(EventService), nameof(EventService.Reset)), postfix: reset);
        harmony.Patch(AccessTools.Method(typeof(EventService), nameof(EventService.Dispose)), postfix: reset);
        return sites == 2 ? null : fallback ?? $"Application hook sites {sites}/2";
    }

    static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> original = InstructionRange.Copy(instructions), code = InstructionRange.Copy(original);
        sites = 0;
        try
        {
            var ranges = new List<(int Start, int Length, List<CodeInstruction> Replacement)>();
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldstr || !Equals(code[i].operand, "statusEffect.apply.")) { continue; }
                ranges.Add(ValidateAndRewrite(code, i));
            }
            if (ranges.Count != 2) { throw new InvalidOperationException("Application hook count changed"); }
            for (int i = ranges.Count - 1; i >= 0; i--)
            {
                var range = ranges[i];
                InstructionRange.Replace(code, range.Start, range.Length, range.Replacement);
            }
            sites = ranges.Count;
            fallback = null;
            return code;
        }
        catch (InvalidOperationException exception) { fallback = exception.Message; return original; }
    }

    static (int Start, int Length, List<CodeInstruction> Replacement) ValidateAndRewrite(List<CodeInstruction> code, int start)
    {
        if (start < 2 || start + 8 >= code.Count || !code[start - 1].Calls(AccessTools.PropertyGetter(typeof(LuaCsSetup), nameof(LuaCsSetup.Hook))) ||
            !code[start - 2].Calls(AccessTools.PropertyGetter(typeof(LuaCsSetup), nameof(LuaCsSetup.Instance))))
        {
            throw new InvalidOperationException("Application hook receiver changed");
        }
        bool item = code[start + 2].Calls(AccessTools.PropertyGetter(typeof(Item), nameof(Item.Prefab)));
        InstructionRange.RequireShape(code, start, item
            ? "ldstr ldloc callvirt ldflda constrained. callvirt call"
            : "ldstr ldloc callvirt stloc ldloca constrained. callvirt call");
        int arguments = start + (item ? 7 : 8);
        int stringCall = arguments - 1;
        if ((!item && !code[start + 2].Calls(AccessTools.PropertyGetter(typeof(Character), nameof(Character.SpeciesName)))) ||
            (item && !code[start + 3].LoadsField(AccessTools.Field(typeof(Prefab), nameof(Prefab.Identifier)), true)) ||
            (!item && InstructionRange.Local(code[start + 3]) != InstructionRange.Local(code[start + 4])) ||
            !Equals(code[stringCall - 2].operand, typeof(Identifier)) ||
            !code[stringCall - 1].Calls(AccessTools.Method(typeof(object), nameof(ToString), Type.EmptyTypes)) ||
            !code[stringCall].Calls(AccessTools.Method(typeof(string), nameof(string.Concat), new[] { typeof(string), typeof(string) })))
        {
            throw new InvalidOperationException("Application hook event name changed");
        }
        InstructionRange.RequireShape(code, arguments,
            "ldc.i4.5 newarr dup ldc.i4.0 ldarg.0 stelem.ref dup ldc.i4.1 ldarg.1 box stelem.ref dup ldc.i4.2 ldloc ldfld stelem.ref dup ldc.i4.3 ldloc ldfld stelem.ref dup ldc.i4.4 ldarg box stelem.ref callvirt");
        FieldInfo entity = code[arguments + 14].operand as FieldInfo;
        FieldInfo targets = code[arguments + 19].operand as FieldInfo;
        if (!Equals(code[arguments + 1].operand, typeof(object)) || !Equals(code[arguments + 9].operand, typeof(float)) ||
            !Equals(code[arguments + 24].operand, typeof(Vector2?)) || !code[arguments + 26].Calls(hookCall) ||
            Convert.ToInt32(code[arguments + 23].operand) != 4 || entity?.FieldType != typeof(Entity) ||
            targets?.FieldType != typeof(IReadOnlyList<ISerializableEntity>) || entity.DeclaringType != targets.DeclaringType ||
            InstructionRange.Local(code[arguments + 13]) != InstructionRange.Local(code[arguments + 18]))
        {
            throw new InvalidOperationException("Application hook argument contract changed");
        }
        var replacement = new List<CodeInstruction>
        {
            new(code[start + 1].opcode, code[start + 1].operand),
            new(code[start + 2].opcode, code[start + 2].operand)
        };
        if (item) { replacement.Add(new CodeInstruction(OpCodes.Ldfld, code[start + 3].operand)); }
        foreach (int offset in new[] { 4, 8, 13, 14, 18, 19, 23 })
        {
            replacement.Add(new CodeInstruction(code[arguments + offset].opcode, code[arguments + offset].operand));
        }
        replacement.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApplicationHooksFeature), nameof(Dispatch))));
        return (start, arguments + 27 - start, replacement);
    }

    static bool? Dispatch(ILuaCsHook hook, Identifier identifier, StatusEffect effect, float deltaTime, Entity entity,
        IReadOnlyList<ISerializableEntity> targets, Vector2? worldPosition) =>
        ApplicationHooks.Call(hook, identifier, effect, deltaTime, entity, targets, worldPosition, on);

    internal override void OnLoadCompleted() => Refresh();
    internal override void RecheckObservers() => Refresh();
    protected override void OnToggled() => Refresh();
    internal override void Uninstall() { on = false; ApplicationHooks.Reset(); }

    static void Refresh()
    {
        observers = null;
        PatchCensus.Foreign body = PatchCensus.Of(apply);
        if (body.Transpilers.Length > 0) { observers = $"{apply}: {body}"; }
        foreach (MethodBase method in new MethodBase[]
        {
            eventCall, eventCall.GetGenericMethodDefinition(),
            AccessTools.Method(typeof(AsyncReaderWriterLock), nameof(AsyncReaderWriterLock.AcquireReaderLock)),
            AccessTools.PropertyGetter(typeof(EventService), nameof(EventService.IsDisposed)),
            AccessTools.Method(typeof(Identifier), nameof(ToString), Type.EmptyTypes),
            AccessTools.Constructor(typeof(EventService.TypeStringKey), new[] { typeof(string) }),
            AccessTools.Method(typeof(EventService.TypeStringKey), "op_Implicit", new[] { typeof(string) }),
            AccessTools.Method(typeof(string), nameof(string.ToLowerInvariant), Type.EmptyTypes)
        })
        {
            PatchCensus.Foreign foreign = PatchCensus.Of(method);
            if (foreign.Any) { observers = $"{method}: {foreign}"; break; }
        }
        on = instance.Active && sites == 2 && observers == null;
        ApplicationHooks.Reset();
    }

    public override string Report() => $"active={on} hooks={sites}/2 skipped={ApplicationHooks.Skipped} forwarded={ApplicationHooks.Forwarded}" +
        (observers != null ? $" disabled: observed by {observers}" : "") + (fallback != null ? $" fallback: {fallback}" : "");
}
