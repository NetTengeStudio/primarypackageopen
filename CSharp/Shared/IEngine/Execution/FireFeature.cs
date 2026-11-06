using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using IEngine.Core;

namespace IEngine.Execution;

sealed class FireFeature : Feature
{
    static FireFeature instance;
    static FieldInfo currentHull;
    static MethodInfo store, setter, damageItems;
    static Harmony harmony;
    static AccessTools.FieldRef<List<Item>, int> listVersion;
    static readonly HashSet<MethodBase> rewritten = new();
    static readonly List<string> problems = new();
    static string observers;
    static bool on, verify;
    static long calls, candidates, scanned, verified, mismatches;

    public override string Name => "fires";

    protected override string Install(Harmony h)
    {
        instance = this;
        harmony = h;
        currentHull = AccessTools.Field(typeof(Item), "currentHull");
        damageItems = AccessTools.Method(typeof(FireSource), "DamageItems");
        store = AccessTools.Method(typeof(HullIndex), nameof(HullIndex.StoreHull));
        setter = AccessTools.PropertySetter(typeof(Item), nameof(Item.CurrentHull));
        if (currentHull == null || currentHull.FieldType != typeof(Hull) || damageItems == null || setter == null) { return "Item.currentHull/FireSource.DamageItems differ in this build"; }
        listVersion = AccessTools.FieldRefAccess<List<Item>, int>("_version");
        RewriteAssembly(typeof(Item).Assembly);
        h.Patch(damageItems, prefix: new HarmonyMethod(typeof(FireFeature), nameof(DamagePrefix)) { priority = Priority.First });
        return null;
    }

    internal override void OnLoadCompleted()
    {
        Assembly self = typeof(FireFeature).Assembly;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a == self || a.IsDynamic || !string.IsNullOrEmpty(a.Location)) { continue; }
            RewriteAssembly(a);
        }
        Refresh();
    }

    protected override void OnToggled() => Refresh();
    internal override void RecheckObservers() => Refresh();

    static void RewriteAssembly(Assembly assembly)
    {
        var hits = IlFieldScanner.ScanAssembly(assembly, currentHull, problems);
        hits.AddRange(IlFieldScanner.ScanCalls(assembly, setter, problems));
        foreach (var hit in hits)
        {
            if (hit.Addresses > 0) { problems.Add($"{hit.Method.DeclaringType?.FullName}.{hit.Method.Name} takes the address of Item.currentHull"); }
            if (hit.Stores == 0 || !rewritten.Add(hit.Method)) { continue; }
            if (hit.Method.ContainsGenericParameters || hit.Method.DeclaringType?.ContainsGenericParameters == true)
            {
                problems.Add($"{hit.Method.DeclaringType?.FullName}.{hit.Method.Name} is generic, writes caught by sweep only");
                continue;
            }
            try { harmony.Patch(hit.Method, transpiler: new HarmonyMethod(typeof(FireFeature), nameof(StoreTranspiler))); }
            catch (Exception e) { problems.Add($"{hit.Method.DeclaringType?.FullName}.{hit.Method.Name}: {e.Message}"); }
        }
    }

    static IEnumerable<CodeInstruction> StoreTranspiler(IEnumerable<CodeInstruction> code)
    {
        foreach (CodeInstruction ci in code)
        {
            if ((ci.opcode == OpCodes.Stfld && ci.operand is FieldInfo f && f == currentHull) || ci.Calls(setter))
            {
                yield return new CodeInstruction(OpCodes.Call, store) { labels = ci.labels, blocks = ci.blocks };
                continue;
            }
            yield return ci;
        }
    }

    public static void Refresh()
    {
        if (instance == null || !instance.Installed) { return; }
        PatchCensus.Foreign f = PatchCensus.Of(damageItems);
        observers = f.Any ? f.ToString() : null;
        bool want = instance.Active && observers == null && ItemRegistry.Tracking;
        if (want && !HullIndex.On) { HullIndex.Start(); }
        if (!want && HullIndex.On) { HullIndex.Stop(); }
        on = want;
    }

    static bool DamagePrefix(FireSource __instance, float deltaTime)
    {
        if (!on || !HullIndex.On || !ItemRegistry.Tracking) { return true; }
#if CLIENT
        if (__instance.size.X <= 0f || GameMain.Client != null) { return false; }
#else
        if (__instance.size.X <= 0f) { return false; }
#endif
        calls++;
        Hull hull = __instance.hull;
        List<Item> list = Item.ItemList;
        int version = listVersion(list);
        if (verify) { Verify(hull); }
        SlotBitSet set = HullIndex.Of(hull);
        if (set == null) { return false; }
        float reach = MathF.Sqrt(__instance.size.X) * 10f;
        float minX = __instance.position.X - reach;
        float maxX = __instance.position.X + __instance.size.X + reach;
        float minY = __instance.position.Y - __instance.size.Y;
        float maxY = (float)hull.Rect.Y;
        Item[] items = ItemRegistry.Items;
        for (int slot = set.Next(0); slot >= 0; slot = set.Next(slot + 1))
        {
            Item item = items[slot];
            candidates++;
            if (item.CurrentHull != hull || item.FireProof || item.Condition <= 0f) { continue; }
            var pos = item.Position;
            if (pos.X < minX || pos.X > maxX || pos.Y < minY || pos.Y > maxY) { continue; }
            Item container = item.Container;
            bool fireProof = false;
            while (container != null)
            {
                if (container.FireProof)
                {
                    fireProof = true;
                    break;
                }
                container = container.Container;
            }
            if (fireProof) { continue; }
            item.ApplyStatusEffects(ActionType.OnFire, deltaTime);
            if (item.Condition <= 0f)
            {
                NetworkMember networkMember = GameMain.NetworkMember;
                if (networkMember != null && networkMember.IsServer)
                {
                    GameMain.NetworkMember.CreateEntityEvent(item, new Item.ApplyStatusEffectEventData(ActionType.OnFire));
                }
            }
            if (listVersion(list) != version) { throw new InvalidOperationException("Collection was modified; enumeration operation may not execute."); }
        }
        return false;
    }

    static void Verify(Hull hull)
    {
        SlotBitSet set = HullIndex.Of(hull);
        foreach (Item item in Item.ItemList)
        {
            scanned++;
            bool inHull = item.CurrentHull == hull;
            bool indexed = ItemRegistry.TryGetSlot(item, out int slot) && set != null && set.Get(slot);
            verified++;
            if (inHull != indexed) { mismatches++; }
        }
    }

    public override void SetVerify(bool value)
    {
        verify = value;
        verified = mismatches = 0;
    }

    public override string Report() =>
        $"active={on} indexing={HullIndex.On} rewritten={rewritten.Count} calls={calls} candidates={candidates} moves={HullIndex.Moves} sweepMissed={HullIndex.Missed}" +
        (observers != null ? $" disabled: observed by {observers}" : "") + (problems.Count > 0 ? $" problems: {string.Join("; ", problems)}" : "") +
        (verify ? $" verified={verified} mismatches={mismatches} indexMismatches={HullIndex.Mismatches()}" : "");
}