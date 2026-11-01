using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace PrimaryPackage.BandSlot {
    public sealed class BandSlotPlugin : PrimaryPackage.Core.IModule, PrimaryPackage.Core.ILoadCompleted {
        public void RoundStart() {
            BandSlotPatches.EnsurePrefabsHaveBandSlot();
            BandSlotPatches.SanitizeAll();
        }

        public void LoadCompleted() {
            BandSlotPatches.EnsurePrefabsHaveBandSlot();
            BandSlotPatches.SanitizeAll();
        }

        public void Attach(Harmony harmony) {
            BandSlotPatches.EnsurePrefabsHaveBandSlot();

            harmony.Patch(
                AccessTools.Constructor(typeof(CharacterInventory), new[] { typeof(ContentXElement), typeof(Character), typeof(bool) }),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(BandSlotPatches), nameof(BandSlotPatches.CharacterInventoryConstructorTranspiler))));
            harmony.Patch(
                AccessTools.Method(typeof(CharacterInventory), "CanBePutInSlot", new[] { typeof(Item), typeof(int), typeof(bool) }),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BandSlotPatches), nameof(BandSlotPatches.CanBePutItemPostfix))));
            harmony.Patch(
                AccessTools.Method(typeof(CharacterInventory), "CanBePutInSlot", new[] { typeof(ItemPrefab), typeof(int), typeof(float?), typeof(int?) }),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BandSlotPatches), nameof(BandSlotPatches.CanBePutPrefabPostfix))));
            harmony.Patch(
                AccessTools.Method(typeof(CharacterInventory), "PutItem", new[] { typeof(Item), typeof(int), typeof(Character), typeof(bool), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BandSlotPatches), nameof(BandSlotPatches.PutItemPrefix))));
            harmony.Patch(
                AccessTools.Method(typeof(CharacterInventory), nameof(CharacterInventory.TryPutItem),
                    new[] { typeof(Item), typeof(Character), typeof(IEnumerable<InvSlotType>), typeof(bool), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BandSlotPatches), nameof(BandSlotPatches.EquipPrefix))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(BandSlotPatches), nameof(BandSlotPatches.EquipFinalizer))));
            harmony.Patch(
                AccessTools.Method(typeof(CharacterInventory), nameof(CharacterInventory.TryPutItem),
                    new[] { typeof(Item), typeof(int), typeof(bool), typeof(bool), typeof(Character), typeof(bool), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(BandSlotPatches), nameof(BandSlotPatches.EquipPrefix))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(BandSlotPatches), nameof(BandSlotPatches.EquipFinalizer))));
            harmony.Patch(
                AccessTools.Method(typeof(Inventory), nameof(Inventory.GetItemAt), new[] { typeof(int) }),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BandSlotPatches), nameof(BandSlotPatches.GetItemAtPostfix))));
#if CLIENT
            harmony.Patch(
                AccessTools.PropertySetter(typeof(CharacterHealth), nameof(CharacterHealth.OpenHealthWindow)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(BandSlotPatches), nameof(BandSlotPatches.OpenHealthWindowPostfix))));
#endif
        }

        public void Release() => BandSlotPatches.Reset();
    }

    internal static class BandSlotPatches {
        private const int MaxDepth = 16;
        internal const InvSlotType MaskSlotType = (InvSlotType)(1 << 30);

        private static readonly Identifier BandTag = "band".ToIdentifier();
        private static readonly MethodInfo ParseSlotTypes = AccessTools.Method(typeof(CharacterInventory), "ParseSlotTypes");
        private static readonly MethodInfo ParseSlotTypesWithBandSlotMethod = AccessTools.Method(typeof(BandSlotPatches), nameof(ParseSlotTypesWithBandSlot));
        private static readonly ConditionalWeakTable<CharacterInventory, StrongBox<int>> PackCache = new();
        private static readonly ConditionalWeakTable<CharacterInventory, StrongBox<int>>.CreateValueCallback ComputePackCached = ComputePack;
        private static readonly FieldInfo SlotsField = AccessTools.Field(typeof(Inventory), "slots");

        private struct MaskFrame {
            public InvSlotType[] Types;
            public int Index;
            public InvSlotType Restore;
        }

        private static readonly MaskFrame[] frames = new MaskFrame[MaxDepth];
        private static int depth;

        public static void EnsurePrefabsHaveBandSlot() {
            try {
                foreach (var prefab in CharacterPrefab.Prefabs) {
                    if (prefab?.ConfigElement == null) { continue; }
                    EnsureElementHasBandSlot(prefab.ConfigElement);
                }
            } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("BandSlot.1", noted); }
        }

        private static void EnsureElementHasBandSlot(ContentXElement root) {
            foreach (var inv in root.GetChildElements("inventory")) {
                string slots = inv.GetAttributeString("slots", null);
                if (slots == null) { continue; }
                string[] parts = slots.Split(',');
                int healthCount = 0;
                for (int i = 0; i < parts.Length; i++) {
                    if (string.Equals(parts[i].Trim(), nameof(InvSlotType.HealthInterface), StringComparison.OrdinalIgnoreCase)) {
                        healthCount++;
                    }
                }
                if (healthCount == 1) {
                    inv.SetAttributeValue("slots", slots + ", HealthInterface");
                }
            }
        }

        public static void SanitizeAll() {
            try {
                foreach (var c in Character.CharacterList) {
                    if (c?.Inventory is CharacterInventory inv) {
                        Sanitize(inv);
                    }
                }
            } catch (Exception noted) { PrimaryPackage.Fault.ModFault.Note("BandSlot.2", noted); }
        }

        internal static void Sanitize(CharacterInventory inv) {
            if (inv == null) { return; }
            int packed = Pack(inv);
            if (packed < 0) { return; }
            int splicerSlot = GeneSlot(packed), bandSlot = BandSlot(packed);
            var slots = SlotsField?.GetValue(inv) as Inventory.ItemSlot[];
            if (slots == null) { return; }

            if (bandSlot >= 0 && bandSlot < slots.Length) {
                var bSlot = slots[bandSlot];
                if (bSlot != null && bSlot.Any()) {
                    bSlot.RemoveWhere(it => it != null && !IsBand(it));
                }
            }

            if (splicerSlot >= 0 && splicerSlot < slots.Length) {
                var sSlot = slots[splicerSlot];
                if (sSlot != null && sSlot.Any()) {
                    List<Item> bandsToMove = null;
                    foreach (var it in sSlot.Items) {
                        if (it != null && IsBand(it)) {
                            bandsToMove ??= new List<Item>();
                            bandsToMove.Add(it);
                        }
                    }
                    if (bandsToMove != null) {
                        sSlot.RemoveWhere(it => it != null && IsBand(it));
                        if (bandSlot >= 0 && bandSlot < slots.Length && slots[bandSlot].Empty()) {
                            for (int k = 0; k < bandsToMove.Count; k++) {
                                slots[bandSlot].Add(bandsToMove[k]);
                            }
                        }
                    }
                }
            }
        }

        internal static void Reset() {
            for (int i = depth - 1; i >= 0; i--) { Unmask(i); }
            depth = 0;
        }

        internal static int Layout(InvSlotType[] types) {
            int first = -1, last = -1, count = 0;
            if (types == null) { return -1; }
            for (int i = 0; i < types.Length; i++) {
                if (types[i] != InvSlotType.HealthInterface && types[i] != MaskSlotType) { continue; }
                if (first < 0) { first = i; }
                last = i;
                count++;
            }
            return count < 2 ? -1 : (first << 16) | last;
        }

        internal static int GeneSlot(int packed) => packed < 0 ? -1 : packed >> 16;

        internal static int BandSlot(int packed) => packed < 0 ? -1 : packed & 0xFFFF;

        public static IEnumerable<CodeInstruction> CharacterInventoryConstructorTranspiler(IEnumerable<CodeInstruction> instructions) {
            foreach (CodeInstruction instruction in instructions) {
                if (instruction.operand is MethodInfo method && method == ParseSlotTypes) {
                    instruction.operand = ParseSlotTypesWithBandSlotMethod;
                }
                yield return instruction;
            }
        }

        public static string[] ParseSlotTypesWithBandSlot(ContentXElement element) {
            string slots = element.GetAttributeString("slots", null);
            if (slots == null) { return Array.Empty<string>(); }

            string[] parsed = slots.Split(',');
            int healthIndex = -1;
            for (int i = 0; i < parsed.Length; i++) {
                if (!string.Equals(parsed[i].Trim(), nameof(InvSlotType.HealthInterface), StringComparison.OrdinalIgnoreCase)) { continue; }
                if (healthIndex >= 0) { return parsed; }
                healthIndex = i;
            }
            if (healthIndex < 0) { return parsed; }

            var expanded = new string[parsed.Length + 1];
            Array.Copy(parsed, 0, expanded, 0, healthIndex);
            expanded[healthIndex] = nameof(InvSlotType.HealthInterface);
            Array.Copy(parsed, healthIndex, expanded, healthIndex + 1, parsed.Length - healthIndex);
            return expanded;
        }

#if CLIENT
        public static void OpenHealthWindowPostfix(CharacterHealth value) {
            if (value?.Character?.Inventory is CharacterInventory inv) {
                inv.RefreshSlotPositions();
                Sanitize(inv);
            }
        }
#endif

        private static StrongBox<int> ComputePack(CharacterInventory inv) => new StrongBox<int>(Layout(inv.SlotTypes));

        private static int Pack(CharacterInventory inv) => PackCache.GetValue(inv, ComputePackCached).Value;

        private static bool IsBand(Item item) =>
            item != null && (item.HasTag(BandTag) || item.Prefab.Identifier.Value.StartsWith("armband", StringComparison.OrdinalIgnoreCase));

        private static bool IsBand(ItemPrefab prefab) =>
            prefab != null && (prefab.Tags.Contains(BandTag) || prefab.Identifier.Value.StartsWith("armband", StringComparison.OrdinalIgnoreCase));

        internal static int OtherSlot(CharacterInventory inv, Item item) {
            int packed = Pack(inv);
            if (packed < 0) { return -1; }
            return IsBand(item) ? GeneSlot(packed) : BandSlot(packed);
        }

        public static void CanBePutItemPostfix(CharacterInventory __instance, Item item, int i, ref bool __result) {
            if (!__result) { return; }
            int packed = Pack(__instance);
            if (packed >= 0 && ((i == BandSlot(packed) && !IsBand(item)) || (i == GeneSlot(packed) && IsBand(item)))) { __result = false; }
        }

        public static void CanBePutPrefabPostfix(CharacterInventory __instance, ItemPrefab itemPrefab, int i, ref bool __result) {
            if (!__result) { return; }
            int packed = Pack(__instance);
            if (packed >= 0 && ((i == BandSlot(packed) && !IsBand(itemPrefab)) || (i == GeneSlot(packed) && IsBand(itemPrefab)))) { __result = false; }
        }

        public static bool PutItemPrefix(CharacterInventory __instance, Item item, ref int i) {
            int packed = Pack(__instance);
            if (packed < 0) { return true; }
            int splicerSlot = GeneSlot(packed), bandSlot = BandSlot(packed);
            if (i == bandSlot && !IsBand(item)) {
                if (__instance.GetItemAt(splicerSlot) == null) {
                    i = splicerSlot;
                    return true;
                }
                return false;
            }
            if (i == splicerSlot && IsBand(item)) {
                if (__instance.GetItemAt(bandSlot) == null) {
                    i = bandSlot;
                    return true;
                }
                return false;
            }
            return true;
        }

        public static void GetItemAtPostfix(Inventory __instance, int index, ref Item __result) {
            if (__result == null || __instance is not CharacterInventory charInv) { return; }
            int packed = Pack(charInv);
            if (packed < 0) { return; }
            if ((index == BandSlot(packed) && !IsBand(__result)) || (index == GeneSlot(packed) && IsBand(__result))) {
                __result = null;
            }
        }

        internal static bool WantsHealthInterface(Item item) {
            if (item == null) { return false; }
            foreach (InvSlotType slot in item.AllowedSlots) {
                if ((slot & InvSlotType.HealthInterface) != 0) { return true; }
            }
            return false;
        }

        public static void EquipPrefix(CharacterInventory __instance, Item item, out bool __state) {
            __state = false;
            if (!WantsHealthInterface(item)) { return; }
            int other = OtherSlot(__instance, item);
            if (other < 0) { return; }
            __state = Mask(__instance.SlotTypes, other);
        }

        public static void EquipFinalizer(CharacterInventory __instance, bool __state) {
            if (__state && depth > 0) {
                depth--;
                Unmask(depth);
            }
            Sanitize(__instance);
        }

        internal static bool Mask(InvSlotType[] types, int index) {
            if (depth >= MaxDepth || types == null || index < 0 || index >= types.Length) { return false; }
            if (types[index] != InvSlotType.HealthInterface) { return false; }
            frames[depth] = new MaskFrame { Types = types, Index = index, Restore = types[index] };
            types[index] = MaskSlotType;
            depth++;
            return true;
        }

        internal static void Unmask() {
            if (depth <= 0) { return; }
            depth--;
            Unmask(depth);
        }

        internal static int Depth => depth;

        private static void Unmask(int frame) {
            InvSlotType[] types = frames[frame].Types;
            if (types != null && frames[frame].Index < types.Length) {
                types[frames[frame].Index] = frames[frame].Restore;
            }
            frames[frame].Types = null;
        }
    }
}
