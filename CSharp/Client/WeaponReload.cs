using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace PrimaryPackage.Reload
{
    public sealed class WeaponReloadPlugin : PrimaryPackage.Core.IModule
    {
        public void RoundStart() => WeaponReload.Clear();

        public void Attach(Harmony harmony)
        {
            harmony.Patch(
                AccessTools.Method(typeof(Character), nameof(Character.ControlLocalPlayer)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(WeaponReload), nameof(WeaponReload.ControlLocalPlayerPostfix))));
        }

        public void Release() => WeaponReload.Clear();
    }

    internal static class WeaponReload
    {
        private const int MaxSearchDepth = 4;

        private static readonly Identifier WeaponTag = "weapon".ToIdentifier();
        private static readonly InvSlotType[] RightHand = { InvSlotType.RightHand };
        private static readonly InvSlotType[] LeftHand = { InvSlotType.LeftHand };
        private static readonly InvSlotType[] BothHands = { InvSlotType.LeftHand, InvSlotType.RightHand };
        private static readonly Comparison<Item> ByConditionDesc = (a, b) => b.ConditionPercentage.CompareTo(a.ConditionPercentage);

        private sealed class RetryEntry
        {
            public Item Mag;
            public int Attempts;
            public double Next;
            public double Expire;
        }

        private sealed class MagState
        {
            public bool Executed;
            public Item Mag;
        }

        private static readonly Dictionary<ushort, RetryEntry> retryQueue = new();
        private static readonly Dictionary<ushort, MagState> magStates = new();
        private static readonly List<ushort> removeKeys = new();
        private static readonly List<Item> findBuffer = new();
        private static readonly List<Item> groupBuffer = new();
        private static readonly List<RelatedItem> ammoSpecs = new();

        internal static void Clear()
        {
            retryQueue.Clear();
            magStates.Clear();
            ammoSpecs.Clear();
        }

        public static void ControlLocalPlayerPostfix(Character __instance)
        {
            if (__instance == null || __instance != Character.Controlled) { return; }
            if (Screen.Selected != GameMain.GameScreen) { return; }
            CharacterInventory chInv = __instance.Inventory;
            if (chInv == null) { return; }
            Item rh = chInv.GetItemInLimbSlot(InvSlotType.RightHand);
            Item lh = chInv.GetItemInLimbSlot(InvSlotType.LeftHand);
            SyncRefilledMags(rh, __instance);
            if (lh != rh) { SyncRefilledMags(lh, __instance); }
            CleanupMagStates();
            TickRetryQueue(__instance);
            if (PrimaryPackage.Core.PlayView.Typing || !__instance.AllowInput || __instance.LockHands) { return; }
            if (!PrimaryPackage.Core.ModSettings.ReloadKey.Hit()) { return; }
            if (rh != null && rh.HasTag(WeaponTag)) { ReloadWeapon(__instance, rh, lh, rh.OwnInventory, RightHand, LeftHand); }
            if (lh != null && lh != rh && lh.HasTag(WeaponTag)) { ReloadWeapon(__instance, lh, rh, lh.OwnInventory, LeftHand, RightHand); }
        }

        private static void SyncRefilledMags(Item weapon, Character ch)
        {
            if (weapon == null || !weapon.HasTag(WeaponTag)) { return; }
            ItemInventory inv = weapon.OwnInventory;
            if (inv?.Container == null) { return; }
            for (int i = 0; i < inv.Capacity; i++)
            {
                Item mag = inv.GetItemAt(i);
                if (mag != null && mag.OwnInventory != null) { return; }
                if (!magStates.TryGetValue(weapon.ID, out MagState state))
                {
                    state = new MagState();
                    magStates[weapon.ID] = state;
                }
                if (mag != null && mag.ConditionIncreasedRecently && !state.Executed)
                {
                    state.Executed = true;
                    state.Mag = mag;
                    inv.TryPutItem(mag, i, allowSwapping: true, allowCombine: false, user: ch);
                }
            }
        }

        private static void CleanupMagStates()
        {
            removeKeys.Clear();
            foreach (var pair in magStates)
            {
                MagState s = pair.Value;
                if (s.Mag != null && (s.Mag.Removed || !s.Mag.ConditionIncreasedRecently))
                {
                    s.Executed = false;
                    s.Mag = null;
                }
                if (!s.Executed && s.Mag == null) { removeKeys.Add(pair.Key); }
            }
            foreach (ushort key in removeKeys) { magStates.Remove(key); }
        }

        private static void TickRetryQueue(Character ch)
        {
            if (GameMain.GameSession == null || !GameMain.GameSession.IsRunning) { return; }
            if (retryQueue.Count == 0) { return; }
            double t = Timing.TotalTime;
            removeKeys.Clear();
            foreach (var pair in retryQueue)
            {
                RetryEntry e = pair.Value;
                if (e.Mag == null || e.Mag.Removed || e.Mag.ID != pair.Key || t > e.Expire)
                {
                    removeKeys.Add(pair.Key);
                    continue;
                }
                if (t < e.Next) { continue; }
                if (TryStack(e.Mag, ch, null, null))
                {
                    removeKeys.Add(pair.Key);
                    continue;
                }
                e.Attempts++;
                e.Next = t + 0.5;
                if (e.Attempts >= 3) { removeKeys.Add(pair.Key); }
            }
            foreach (ushort key in removeKeys) { retryQueue.Remove(key); }
        }

        private static bool FillAmmoSpecs(Item weapon)
        {
            ammoSpecs.Clear();
            foreach (ItemComponent ic in weapon.Components)
            {
                if (ic.RequiredItems == null) { continue; }
                if (!ic.RequiredItems.TryGetValue(RelatedItem.RelationType.Contained, out List<RelatedItem> list)) { continue; }
                foreach (RelatedItem ri in list)
                {
                    if (!ammoSpecs.Contains(ri)) { ammoSpecs.Add(ri); }
                }
            }
            return ammoSpecs.Count > 0;
        }

        private static bool IsAmmo(Item item)
        {
            for (int i = 0; i < ammoSpecs.Count; i++)
            {
                if (ammoSpecs[i].MatchesItem(item)) { return true; }
            }
            return false;
        }

        private static bool ForItems(Character ch, Item h1, Item h2, Predicate<Item> fn) => Scan(ch.Inventory, h1, h2, fn, 0);

        private static bool Scan(Inventory inv, Item h1, Item h2, Predicate<Item> fn, int depth)
        {
            foreach (Item it in inv.AllItemsMod)
            {
                if (it == null || it == h1 || it == h2 || it.HasTag(WeaponTag)) { continue; }
                if (fn(it)) { return true; }
                if (it.OwnInventory == null || depth >= MaxSearchDepth) { continue; }
                if (Scan(it.OwnInventory, h1, h2, fn, depth + 1)) { return true; }
            }
            return false;
        }

        private static bool StackInto(Item mag, Inventory inv)
        {
            Identifier id = mag.Prefab.Identifier;
            for (int i = 0; i < inv.Capacity; i++)
            {
                var items = inv.slots[i].Items;
                for (int j = 0; j < items.Count; j++)
                {
                    Item it = items[j];
                    if (it != mag && !it.HasTag(WeaponTag) && it.Prefab.Identifier == id
                        && it.ConditionPercentage == 0 && inv.CanBePutInSlot(mag, i))
                    {
                        return inv.TryPutItem(mag, i, allowSwapping: false, allowCombine: true, user: null);
                    }
                }
            }
            return false;
        }

        private static bool TryStack(Item mag, Character ch, Item h1, Item h2)
        {
            if (mag == null || mag.Removed || mag.ConditionPercentage > 0) { return false; }
            if (StackInto(mag, ch.Inventory)) { return true; }
            return ForItems(ch, h1, h2, it => it.OwnInventory != null && StackInto(mag, it.OwnInventory));
        }

        private static bool AddToSlot(Item mag, Character ch)
        {
            CharacterInventory inv = ch.Inventory;
            for (int i = inv.Capacity - 1; i >= 0; i--)
            {
                InvSlotType t = inv.SlotTypes[i];
                if ((t == InvSlotType.InnerClothes || t == InvSlotType.OuterClothes || t == InvSlotType.Bag)
                    && inv.TryPutItem(mag, i, allowSwapping: false, allowCombine: true, user: null)) { return true; }
            }
            for (int i = inv.Capacity - 1; i >= 0; i--)
            {
                if (inv.SlotTypes[i] == InvSlotType.Any && inv.CanBePutInSlot(mag, i))
                {
                    inv.TryPutItem(mag, i, allowSwapping: false, allowCombine: false, user: null);
                    return true;
                }
            }
            return false;
        }

        private static bool Eject(Character ch, ItemInventory inv, int index, Item h1, Item h2)
        {
            Item mag = inv.GetItemAt(index);
            if (mag == null) { return false; }
            if (TryStack(mag, ch, h1, h2)) { return true; }
            retryQueue[mag.ID] = new RetryEntry { Mag = mag, Next = Timing.TotalTime + 0.5, Expire = Timing.TotalTime + 5.0 };
            if (AddToSlot(mag, ch)) { return true; }
            mag.Drop(ch);
            return false;
        }

        private static int FindItems(Character ch, Item h1, Item h2, Predicate<Item> filter, int limit = 0)
        {
            findBuffer.Clear();
            ForItems(ch, h1, h2, it =>
            {
                if (!filter(it)) { return false; }
                findBuffer.Add(it);
                return limit > 0 && findBuffer.Count >= limit;
            });
            return findBuffer.Count;
        }

        private static bool TryPut(Item it, ItemInventory inv, int index, bool swap, bool combine, Character ch)
        {
            if (it == null || it.Removed || it.ConditionPercentage <= 0) { return false; }
            return inv.TryPutItem(it, index, swap, combine, ch);
        }

        private static void RestoreHands(Character ch, Item h1, Item h2, InvSlotType[] h1Slots, InvSlotType[] h2Slots)
        {
            CharacterInventory inv = ch.Inventory;
            if (inv.GetItemInLimbSlot(h1Slots[0]) == h1 && inv.GetItemInLimbSlot(h2Slots[0]) == h2) { return; }
            if (h1 != null && h1 == h2)
            {
                foreach (InvSlotType slot in BothHands)
                {
                    int i = inv.FindLimbSlot(slot);
                    if (i >= 0) { inv.TryPutItem(h1, i, allowSwapping: true, allowCombine: false, user: ch); }
                }
                return;
            }
            if (h1 != null) { inv.TryPutItem(h1, ch, h1Slots); }
            if (h2 != null) { inv.TryPutItem(h2, ch, h2Slots); }
        }

        private static void FillEmptySlot(Character ch, Item h1, Item h2, ItemInventory inv, int index, int max, bool typed)
        {
            int n = FindItems(ch, h1, h2, it => (!typed || IsAmmo(it)) && it.ConditionPercentage > 0 && inv.CanBePutInSlot(it, index));
            if (n == 0) { return; }
            Identifier bestId = Identifier.Empty;
            int bestCount = 0;
            for (int i = 0; i < n; i++)
            {
                Identifier id = findBuffer[i].Prefab.Identifier;
                int c = 0;
                for (int j = 0; j < n; j++)
                {
                    if (findBuffer[j].Prefab.Identifier == id) { c++; }
                }
                if (c > bestCount) { bestCount = c; bestId = id; }
            }
            groupBuffer.Clear();
            for (int i = 0; i < n; i++)
            {
                if (findBuffer[i].Prefab.Identifier == bestId) { groupBuffer.Add(findBuffer[i]); }
            }
            groupBuffer.Sort(ByConditionDesc);
            for (int i = 0; i < groupBuffer.Count && i < max; i++) { TryPut(groupBuffer[i], inv, index, false, false, ch); }
        }

        private static void ProcessSlot(Character ch, Item h1, Item h2, ItemInventory inv, int index, ItemContainer container, InvSlotType[] h1Slots, InvSlotType[] h2Slots, bool typed)
        {
            if (index < 0 || index >= inv.Capacity) { return; }
            int max = container.GetMaxStackSize(index);
            if (max <= 0) { return; }
            var slotItems = inv.slots[index].Items;
            int count = slotItems.Count;

            if (count == 0)
            {
                FillEmptySlot(ch, h1, h2, inv, index, max, typed);
                return;
            }

            Item mag = slotItems[0];
            Identifier magId = mag.Prefab.Identifier;

            if (count < max)
            {
                int n = FindItems(ch, h1, h2, it => it.Prefab.Identifier == magId && it.ConditionPercentage > 0);
                if (n > 1) { findBuffer.Sort(ByConditionDesc); }
                groupBuffer.Clear();
                for (int i = 0; i < n && i < max - count; i++) { groupBuffer.Add(findBuffer[i]); }
                foreach (Item it in groupBuffer) { TryPut(it, inv, index, false, false, ch); }
                return;
            }

            if (mag.ConditionPercentage >= 100) { return; }
            int rn = FindItems(ch, h1, h2, it => it.Prefab.Identifier == magId && it.ConditionPercentage > 0);
            if (rn > 1) { findBuffer.Sort(ByConditionDesc); }
            Item candidate = rn > 0 ? findBuffer[0] : null;
            bool depleted = mag.ConditionPercentage <= 0;
            if (candidate == null)
            {
                if (depleted) { Eject(ch, inv, index, h1, h2); }
                return;
            }
            if (!depleted && candidate.ConditionPercentage <= mag.ConditionPercentage) { return; }
            if (!TryPut(candidate, inv, index, true, true, ch))
            {
                Eject(ch, inv, index, h1, h2);
                RestoreHands(ch, h1, h2, h1Slots, h2Slots);
                TryPut(candidate, inv, index, true, true, ch);
            }
            TryStack(mag, ch, h1, h2);
        }

        private static void ReloadWeapon(Character ch, Item h1, Item h2, ItemInventory inv, InvSlotType[] h1Slots, InvSlotType[] h2Slots)
        {
            ItemContainer container = inv?.Container;
            if (container == null || inv.Capacity == 0) { return; }
            bool typed = FillAmmoSpecs(h1);
            int start = Math.Clamp(container.ContainedStateIndicatorSlot, 0, inv.Capacity - 1);
            ProcessSlot(ch, h1, h2, inv, start, container, h1Slots, h2Slots, typed);
            if (!typed) { return; }
            for (int i = 0; i < inv.Capacity; i++)
            {
                if (i != start) { ProcessSlot(ch, h1, h2, inv, i, container, h1Slots, h2Slots, true); }
            }
        }
    }
}
