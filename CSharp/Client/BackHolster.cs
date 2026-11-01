using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace PrimaryPackage.BackHolster {
    public sealed class BackHolsterPlugin : PrimaryPackage.Core.IModule {
        private const float BackShiftPx = 3f;
        private static readonly Identifier Tag = "pp_backholster".ToIdentifier();
        private static readonly ConditionalWeakTable<Item, State> States = new();
        private static readonly List<State> Attached = new();
                private sealed class State {
            public WearableSprite Ws;
            public WearableSprite PackWs;
            public Item Weapon;
            public Limb Limb;
            public Character Picker;
        }

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            try {
                harmony.Patch(AccessTools.Method(typeof(Wearable), nameof(Wearable.Equip)),
                    postfix: new HarmonyMethod(typeof(BackHolsterPlugin), nameof(WearablePostfix)));
                harmony.Patch(AccessTools.Method(typeof(Wearable), nameof(Wearable.Unequip)),
                    postfix: new HarmonyMethod(typeof(BackHolsterPlugin), nameof(WearablePostfix)));
                harmony.Patch(AccessTools.Method(typeof(ItemContainer), nameof(ItemContainer.OnItemContained)),
                    postfix: new HarmonyMethod(typeof(BackHolsterPlugin), nameof(ContainerPostfix)));
                harmony.Patch(AccessTools.Method(typeof(ItemContainer), nameof(ItemContainer.OnItemRemoved)),
                    postfix: new HarmonyMethod(typeof(BackHolsterPlugin), nameof(ContainerPostfix)));
            }
            catch (Exception e) {
                DebugConsole.ThrowError("backholster - не удалось установить патчи", e);
            }
        }


        public void Release() {
            foreach (var st in Attached) { st.Limb?.WearingItems.Remove(st.Ws); }
            Attached.Clear();
        }

        public static void WearablePostfix(Wearable __instance, Character character) {
            Refresh(__instance.Item);
            for (int i = Attached.Count - 1; i >= 0; i--) {
                var st = Attached[i];
                if (st.Picker is { Removed: true }) { Detach(st); continue; }
                if (st.Picker == character && st.Limb != null) { Position(st); }
            }
        }

        public static void ContainerPostfix(ItemContainer __instance) => Refresh(__instance.Item);

        private static void Refresh(Item pack) {
            if (!pack.HasTag(Tag)) { return; }
            var wearable = pack.GetComponent<Wearable>();
            var container = pack.GetComponent<ItemContainer>();
            if (wearable == null || container == null) { return; }
            var st = States.GetOrCreateValue(pack);
            Item weapon = null;
            for (int i = container.MainContainerCapacity; i < container.Capacity; i++) {
                var it = container.Inventory.GetItemAt(i);
                if (it != null) { weapon = it; break; }
            }
            Character picker = wearable.IsActive ? wearable.picker : null;
            Limb torso = picker is { Removed: false } ? picker.AnimController.GetLimb(LimbType.Torso) : null;
            bool want = torso != null && weapon != null;
            if (st.Limb != null && (!want || st.Limb != torso || st.Weapon != weapon)) { Detach(st); }
            if (st.Weapon != weapon) {
                st.Ws?.Remove();
                st.Ws = null;
                st.Weapon = weapon;
            }
            if (!want || st.Limb != null) { return; }
            st.Ws ??= Build(weapon, wearable);
            if (st.Ws == null) { return; }
            if (!st.Ws.IsInitialized) { st.Ws.Init(picker); }
            st.Ws.Picker = picker;
            if (st.Ws.Sprite == null) { return; }
            st.PackWs = FindTorsoSprite(wearable);
            st.Limb = torso;
            st.Picker = picker;
            Attached.Add(st);
            Position(st);
        }

        private static void Position(State st) {
            var list = st.Limb.WearingItems;
            list.Remove(st.Ws);
            int idx = st.PackWs != null ? list.IndexOf(st.PackWs) : -1;
            if (idx < 0) { idx = list.Count; }
            list.Insert(idx, st.Ws);
        }

        private static void Detach(State st) {
            st.Limb.WearingItems.Remove(st.Ws);
            st.Limb = null;
            st.Picker = null;
            st.PackWs = null;
            Attached.Remove(st);
        }

        private static WearableSprite FindTorsoSprite(Wearable w) {
            if (w?.wearableSprites == null) { return null; }
            for (int i = 0; i < w.wearableSprites.Length; i++) {
                if (w.limbType[i] == LimbType.Torso && w.wearableSprites[i] != null) { return w.wearableSprites[i]; }
            }
            return null;
        }

        private static WearableSprite Build(Item weapon, Wearable packWearable) {
            var ci = CultureInfo.InvariantCulture;
            XElement xe = null;
            ContentPackage cp = null;
            var srcWs = FindTorsoSprite(weapon.GetComponent<Wearable>());
            if (srcWs?.SourceElement != null) {
                xe = new XElement(srcWs.SourceElement.Element);
                cp = srcWs.SourceElement.ContentPackage;
            }
            if (xe == null) {
                var s = weapon.Prefab.Sprite;
                if (s == null) { return null; }
                xe = new XElement("sprite",
                    new XAttribute("texture", s.FullPath),
                    new XAttribute("limb", "Torso"),
                    new XAttribute("sourcerect", string.Format(ci, "{0},{1},{2},{3}", s.SourceRect.X, s.SourceRect.Y, s.SourceRect.Width, s.SourceRect.Height)),
                    new XAttribute("origin", "0.5,0.75"),
                    new XAttribute("rotation", "90"),
                    new XAttribute("scale", weapon.Scale.ToString(ci)),
                    new XAttribute("canbehiddenbyotherwearables", "false"));
                cp = weapon.Prefab.ContentPackage;
            }
            ShiftOrigin(xe, ci);
            xe.SetAttributeValue("inheritlimbdepth", "true");
            xe.SetAttributeValue("depthlimb", "LeftFoot");
            xe.SetAttributeValue("depth", null);
            var ws = new WearableSprite(new ContentXElement(cp, xe), WearableType.Item);
            ws.WearableComponent = packWearable;
            return ws;
        }

        private static void ShiftOrigin(XElement xe, CultureInfo ci) {
            string[] rect = (xe.Attribute("sourcerect")?.Value ?? "").Split(',');
            string[] orig = (xe.Attribute("origin")?.Value ?? "0.5,0.5").Split(',');
            if (rect.Length != 4 || orig.Length != 2) { return; }
            if (!float.TryParse(rect[3], NumberStyles.Float, ci, out float h) || h <= 0f) { return; }
            if (!float.TryParse(orig[0], NumberStyles.Float, ci, out float ox)) { return; }
            if (!float.TryParse(orig[1], NumberStyles.Float, ci, out float oy)) { return; }
            xe.SetAttributeValue("origin", string.Format(ci, "{0},{1}", ox, oy + BackShiftPx / h));
        }
    }
}
