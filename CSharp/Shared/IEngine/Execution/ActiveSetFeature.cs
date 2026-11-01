using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using MoonSharp.Interpreter.Interop;
using PrimaryPackage.Core;

namespace IEngine.Execution;

sealed class ActiveSetFeature : Feature
{
    static ActiveSetFeature instance;
    static FieldInfo isActive;
    static MethodInfo store, itemUpdate;
    static Harmony harmony;
    static readonly HashSet<MethodBase> rewritten = new();
    static readonly List<string> problems = new();
    static string observers;
    static bool verify;
    static long verifyTicks, uncovered, misordered;

    public override string Name => "activeset";

    protected override string Install(Harmony h)
    {
        instance = this;
        harmony = h;
        isActive = AccessTools.Field(typeof(Item), nameof(Item.IsActive));
        if (isActive == null || isActive.FieldType != typeof(bool)) { return "Item.IsActive is not a bool field in this build"; }
        store = AccessTools.Method(typeof(ItemRegistry), nameof(ItemRegistry.StoreIsActive));
        itemUpdate = AccessTools.Method(typeof(Item), nameof(Item.Update));
        RewriteAssembly(typeof(Item).Assembly);
        foreach (ConstructorInfo ctor in typeof(Item).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            h.Patch(ctor, transpiler: new HarmonyMethod(typeof(ActiveSetFeature), nameof(ListedTranspiler)));
        }
        h.Patch(AccessTools.Method(typeof(Item), "RemoveFromLists"), postfix: new HarmonyMethod(typeof(ActiveSetFeature), nameof(Unlisted)));
        var luaSetter = AccessTools.Method(typeof(FieldMemberDescriptor), nameof(FieldMemberDescriptor.SetValue));
        if (luaSetter != null) { h.Patch(luaSetter, postfix: new HarmonyMethod(typeof(ActiveSetFeature), nameof(LuaFieldSet))); }
        else { problems.Add("MoonSharp FieldMemberDescriptor.SetValue not found, Lua writes caught by sweep only"); }
        return null;
    }

    internal override void OnLoadCompleted()
    {
        Assembly self = typeof(ActiveSetFeature).Assembly;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a == self || a.IsDynamic || !string.IsNullOrEmpty(a.Location)) { continue; }
            RewriteAssembly(a);
        }
        ItemUpdatePolicy.Invalidated -= ObserverChanged;
        ItemUpdatePolicy.Invalidated += ObserverChanged;
        Refresh();
    }

    static void RewriteAssembly(Assembly assembly)
    {
        foreach (var hit in IlFieldScanner.ScanAssembly(assembly, isActive, problems))
        {
            if (hit.Addresses > 0) { problems.Add($"{hit.Method.DeclaringType?.FullName}.{hit.Method.Name} takes the address of Item.IsActive"); }
            if (hit.Stores == 0 || !rewritten.Add(hit.Method)) { continue; }
            if (hit.Method.ContainsGenericParameters || hit.Method.DeclaringType?.ContainsGenericParameters == true)
            {
                problems.Add($"{hit.Method.DeclaringType?.FullName}.{hit.Method.Name} is generic, writes caught by sweep only");
                continue;
            }
            try { harmony.Patch(hit.Method, transpiler: new HarmonyMethod(typeof(ActiveSetFeature), nameof(StoreTranspiler))); }
            catch (Exception e) { problems.Add($"{hit.Method.DeclaringType?.FullName}.{hit.Method.Name}: {e.Message}"); }
        }
    }

    static IEnumerable<CodeInstruction> StoreTranspiler(IEnumerable<CodeInstruction> code)
    {
        foreach (CodeInstruction ci in code)
        {
            if (ci.opcode == OpCodes.Stfld && ci.operand is FieldInfo f && f == isActive)
            {
                yield return new CodeInstruction(OpCodes.Call, store) { labels = ci.labels, blocks = ci.blocks };
                continue;
            }
            yield return ci;
        }
    }

    static IEnumerable<CodeInstruction> ListedTranspiler(IEnumerable<CodeInstruction> code)
    {
        var list = code.ToList();
        FieldInfo itemList = AccessTools.Field(typeof(Item), nameof(Item.ItemList));
        MethodInfo add = AccessTools.Method(typeof(List<Item>), nameof(List<Item>.Add));
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].LoadsField(itemList)) { continue; }
            for (int j = i + 1; j < list.Count && j < i + 4; j++)
            {
                if (!list[j].Calls(add)) { continue; }
                list.InsertRange(j + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ItemRegistry), nameof(ItemRegistry.OnListed)))
                });
                break;
            }
        }
        return list;
    }

    static void Unlisted(Item __instance) => ItemRegistry.OnUnlisted(__instance);

    static void LuaFieldSet(FieldMemberDescriptor __instance, object obj)
    {
        if (obj is Item item && item.IsActive && __instance.FieldInfo == isActive) { ItemRegistry.StoreIsActive(item, true); }
    }

    public static void Refresh()
    {
        if (instance == null || !instance.Installed) { return; }
        var foreign = PatchCensus.Of(itemUpdate, ItemUpdatePolicy.CanSkipInactive);
        observers = foreign.Observers ? foreign.ToString() : null;
        bool on = instance.Active && observers == null;
        if (on && !ItemRegistry.Tracking) { ItemRegistry.Start(); }
        if (!on && ItemRegistry.Tracking) { ItemRegistry.Stop(); }
        MapEntityExecutor.ActiveSet = on;
        Simulation.ContainerFeature.Refresh();
        Simulation.SpatialFeature.Refresh();
        FireFeature.Refresh();
    }

    protected override void OnToggled() => Refresh();
    internal override void RecheckObservers() => Refresh();

    static void ObserverChanged(object entity)
    {
        if (entity == null)
        {
            Refresh();
            Simulation.Dormancy.WakeAll(Simulation.Dormancy.All);
            return;
        }
        if (entity is not Item item) { return; }
        Simulation.Dormancy.MarkDirty("update observer");
        if (!ItemRegistry.TryGetSlot(item, out int slot)) { return; }
        bool spatial = (Simulation.Dormancy.ReasonsOf(slot) & Simulation.Dormancy.BySpatial) != 0;
        Simulation.Dormancy.WakeSlot(slot, item, Simulation.Dormancy.All);
        if (!spatial && Simulation.SpatialCore.On) { Simulation.SpatialCore.OnEventWake(slot); }
    }

    internal override void Uninstall()
    {
        ItemUpdatePolicy.Invalidated -= ObserverChanged;
        Diagnostics.Probes.TickEnded -= VerifyTick;
        verify = false;
        MapEntityExecutor.ActiveSet = false;
        ItemRegistry.Stop();
    }

    public override void SetVerify(bool on)
    {
        verify = on;
        verifyTicks = uncovered = misordered = 0;
        Diagnostics.Probes.TickEnded -= VerifyTick;
        if (on) { Diagnostics.Probes.TickEnded += VerifyTick; }
    }

    static void VerifyTick()
    {
        if (!ItemRegistry.Tracking) { return; }
        verifyTicks++;
        uncovered += ItemRegistry.VerifyCoverage();
        if (!ItemRegistry.VerifyOrder()) { misordered++; }
    }

    public override string Report() =>
        $"tracking={ItemRegistry.Tracking} rewritten={rewritten.Count} registered={ItemRegistry.Count} active={ItemRegistry.Active.PopCount()} " +
        $"visited={MapEntityExecutor.ItemsVisited} updated={MapEntityExecutor.ItemsUpdated} rebuilds={ItemRegistry.Rebuilds} compactions={ItemRegistry.Compactions} sweepMissed={ItemRegistry.Missed}" +
        (observers != null ? $" disabled: Item.Update observed by {observers}" : "") +
        (problems.Count > 0 ? $" problems: {string.Join("; ", problems.Take(8))}" : "") +
        (verify ? $" verifyTicks={verifyTicks} uncovered={uncovered} misordered={misordered}" : "") +
        $" itemUpdatePatches: {PatchCensus.Of(itemUpdate)}";
}
