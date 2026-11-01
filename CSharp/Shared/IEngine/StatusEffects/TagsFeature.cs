using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using HarmonyLib;

namespace IEngine.StatusEffects;

sealed class TagsFeature : Feature
{
    sealed class Memo
    {
        public string Value;
        public HashSet<Identifier> Set;
        public int Version;
    }

    static readonly AccessTools.FieldRef<HashSet<Identifier>, int> SetVersion = AccessTools.FieldRefAccess<HashSet<Identifier>, int>("_version");
    static readonly ConditionalWeakTable<Item, Memo> memos = new();
    static TagsFeature instance;
    static long skipped, applied;

    public override string Name => "tags";

    protected override string Install(Harmony harmony)
    {
        instance = this;
        var setter = AccessTools.PropertySetter(typeof(Item), nameof(Item.Tags));
        if (setter == null) { return "Item.Tags setter not found"; }
        harmony.Patch(setter,
            prefix: new HarmonyMethod(typeof(TagsFeature), nameof(Prefix)) { priority = Priority.First },
            postfix: new HarmonyMethod(typeof(TagsFeature), nameof(Postfix)) { priority = Priority.Last });
        return null;
    }

    static bool Prefix(Item __instance, string value, bool __runOriginal)
    {
        if (!__runOriginal || value == null || !instance.Active) { return true; }
        if (memos.TryGetValue(__instance, out Memo m) && ReferenceEquals(m.Set, __instance.tags) && SetVersion(m.Set) == m.Version && string.Equals(m.Value, value))
        {
            skipped++;
            return false;
        }
        return true;
    }

    static void Postfix(Item __instance, string value, bool __runOriginal)
    {
        if (!__runOriginal || value == null || !instance.Active) { return; }
        applied++;
        Memo m = memos.GetOrCreateValue(__instance);
        m.Value = value;
        m.Set = __instance.tags;
        m.Version = SetVersion(m.Set);
    }

    public override string Report() => $"skipped={skipped} applied={applied}";
}
