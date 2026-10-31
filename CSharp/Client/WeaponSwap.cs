using System;
using System.Reflection;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using HarmonyLib;

namespace PrimaryPackage.WeaponSwap {
    public sealed class WeaponSwapPlugin : PrimaryPackage.Core.IModule, IEventKeyUpdate {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            MethodInfo target = AccessTools.Method(typeof(Character), "UpdateProjSpecific");
            if (target != null && GmHud.IsSupported) {
                harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(GmHud), nameof(GmHud.Postfix))));
            }
            LuaCsSetup.Instance?.EventService?.Subscribe<IEventKeyUpdate>(this);
        }
        public void OnKeyUpdate(double deltaTime) => WeaponSwap.Update();
        public void Release() {
            LuaCsSetup.Instance?.EventService?.Unsubscribe<IEventKeyUpdate>(this);
            WeaponSwap.Reset();
        }
    }

    internal static class GmHud {
        private static readonly AccessTools.FieldRef<Character, bool> HudInfoVisible = Field();
        private static readonly Action<CharacterParams, bool> SetBossBar = Setter();
        private static double cacheTime = -1.0;
        private static bool cacheValue;

        internal static bool IsSupported => HudInfoVisible != null;

        private static AccessTools.FieldRef<Character, bool> Field() {
            try { return AccessTools.FieldRefAccess<Character, bool>("hudInfoVisible"); }
            catch { return null; }
        }
        private static Action<CharacterParams, bool> Setter() {
            MethodInfo m = AccessTools.PropertySetter(typeof(CharacterParams), nameof(CharacterParams.UseBossHealthBar));
            if (m == null) { return null; }
            try { return (Action<CharacterParams, bool>)m.CreateDelegate(typeof(Action<CharacterParams, bool>)); }
            catch { return null; }
        }
        private static bool IsGm() {
            GameClient cl = GameMain.Client;
            if (cl == null || !cl.HasPermission(ClientPermissions.ConsoleCommands)) { return false; }
            Character c = Character.Controlled;
            return c == null || c.IsDead;
        }
        public static void Postfix(Character __instance) {
            if (__instance == null || __instance.Removed) { return; }
            double t = Timing.TotalTime;
            if (t != cacheTime) { cacheTime = t; cacheValue = IsGm(); }
            bool g = cacheValue;
            ref bool visible = ref HudInfoVisible(__instance);
            if (visible != g) { visible = g; }
            CharacterParams p = __instance.Params;
            if (SetBossBar != null && p != null && p.UseBossHealthBar != g) { SetBossBar(p, g); }
        }
    }

    internal static class WeaponSwap {
        private static readonly Identifier WeaponTag = "weapon".ToIdentifier();
        private static readonly Identifier GunTag = "gun".ToIdentifier();
        private static readonly Identifier MediumTag = "mediumitem".ToIdentifier();
        private static readonly Identifier GrenadeTag = "handgrenade".ToIdentifier();
        private static readonly Identifier ExplosiveTag = "explosive".ToIdentifier();
        private static readonly InvSlotType[] AnySlot = { InvSlotType.Any };
        private static readonly InvSlotType[] StorageTypes = { InvSlotType.Bag, InvSlotType.InnerClothes, InvSlotType.OuterClothes };
        private static readonly int[] handSlot = { -1, -1 };
        private static readonly int[] storageSlot = { -1, -1, -1 };
        private static readonly Inventory[] subInvs = new Inventory[3];
        private static int subInvN;
        private static Character activeChar;
        private static Item lastHolstered;

        internal static void Reset() {
            activeChar = null;
            lastHolstered = null;
            Array.Clear(subInvs, 0, subInvs.Length);
            subInvN = 0;
        }

        private static bool Is(Item item, Identifier tag) => item.Prefab.Identifier == tag || item.HasTag(tag);

        private static void RefreshSlotCache(Character ch) {
            CharacterInventory inv = ch.Inventory;
            activeChar = ch;
            handSlot[0] = inv.FindLimbSlot(InvSlotType.LeftHand);
            handSlot[1] = inv.FindLimbSlot(InvSlotType.RightHand);
            for (int i = 0; i < 3; i++) { storageSlot[i] = inv.FindLimbSlot(StorageTypes[i]); }
        }

        private static void GatherSubInventories(CharacterInventory inv) {
            subInvN = 0;
            for (int i = 0; i < 3; i++) {
                Item it = inv.GetItemAt(storageSlot[i]);
                Inventory own = it?.OwnInventory;
                if (own != null) { subInvs[subInvN++] = own; }
            }
            for (int i = subInvN; i < subInvs.Length; i++) { subInvs[i] = null; }
        }

        private static bool StowToAny(Item item, CharacterInventory inv, Character ch) {
            if (inv.TryPutItem(item, ch, AnySlot)) { return true; }
            for (int i = 0; i < subInvN; i++) {
                if (subInvs[i].TryPutItem(item, ch, AnySlot)) { return true; }
            }
            return false;
        }

        private static bool Holster(Item weapon, CharacterInventory inv, Character ch) {
            for (int i = 0; i < subInvN; i++) {
                int s = subInvs[i].FindAllowedSlot(weapon);
                if (s != -1 && subInvs[i].TryPutItem(weapon, s, false, false, ch)) { return true; }
            }
            return storageSlot[0] >= 0 && inv.TryPutItem(weapon, storageSlot[0], false, false, ch);
        }

        private static bool Draw(Item item, CharacterInventory inv, Character ch) {
            if (item == null || !Is(item, WeaponTag)) { return false; }
            for (int i = 0; i < 2; i++) {
                if (handSlot[i] >= 0 && inv.TryPutItem(item, handSlot[i], false, false, ch)) { return true; }
            }
            for (int i = 0; i < 2; i++) {
                if (handSlot[i] < 0) { continue; }
                Item held = inv.GetItemAt(handSlot[i]);
                if (held != null && StowToAny(held, inv, ch) && inv.TryPutItem(item, handSlot[i], false, false, ch)) { return true; }
            }
            return false;
        }

        private static int Score(Item item) {
            if (item == null || !Is(item, WeaponTag)) { return -1; }
            if (Is(item, GunTag)) { return Is(item, MediumTag) ? 4 : 3; }
            if (Is(item, GrenadeTag) || Is(item, ExplosiveTag)) { return 1; }
            return 2;
        }

        private static bool Consider(Item item, ref Item best, ref int bestScore) {
            if (item == null) { return false; }
            if (item == lastHolstered) { best = item; bestScore = int.MaxValue; return true; }
            int s = Score(item);
            if (s > bestScore) { best = item; bestScore = s; }
            return false;
        }

        internal static void Update() {
            if (Screen.Selected != GameMain.GameScreen) { return; }
            if (!PlayerInput.KeyHit(InputType.ToggleInventory) || PrimaryPackage.Core.PlayView.Typing) { return; }
            Character c = Character.Controlled;
            CharacterInventory inv = c?.Inventory;
            if (inv == null) { return; }
            if (c != activeChar) { RefreshSlotCache(c); }
            if (lastHolstered is { Removed: true }) { lastHolstered = null; }
            GatherSubInventories(inv);
            for (int i = 1; i >= 0; i--) {
                if (handSlot[i] < 0) { continue; }
                Item held = inv.GetItemAt(handSlot[i]);
                if (held == null || !Is(held, WeaponTag)) { continue; }
                if (Holster(held, inv, c)) { lastHolstered = held; }
                return;
            }
            Item best = null;
            int bestScore = 0;
            for (int i = 0; i < 3; i++) {
                if (Consider(inv.GetItemAt(storageSlot[i]), ref best, ref bestScore)) { goto found; }
            }
            for (int i = 0; i < subInvN; i++) {
                Inventory sub = subInvs[i];
                for (int s = 0, n = sub.Capacity; s < n; s++) {
                    if (Consider(sub.GetItemAt(s), ref best, ref bestScore)) { goto found; }
                }
            }
            found:
            if (best != null) { Draw(best, inv, c); }
        }
    }
}
