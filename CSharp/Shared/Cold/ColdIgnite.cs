using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Cold {
    public static class ColdIgnite {
        private static readonly HashSet<Identifier> igniters = new HashSet<Identifier>();
        private static Identifier materialId = Identifier.Empty;
        private static float duration = 18.0f;
        private static float damagePerSecond = 4.0f;
        private static float pileRadius = 60.0f;
        private static float warmthTarget = 32.0f;
        private static float warmthRate = 0.06f;

        private static readonly Dictionary<Item, float> burning = new Dictionary<Item, float>();
        private static readonly Dictionary<ItemPrefab, bool> flammable = new Dictionary<ItemPrefab, bool>();
        private static readonly List<Item> stale = new List<Item>();
        private static readonly List<KeyValuePair<Item, float>> updates = new List<KeyValuePair<Item, float>>();
        private static float timer;

        public static void Load(XElement root) {
            burning.Clear();
            flammable.Clear();
            igniters.Clear();
            materialId = Identifier.Empty;
            XElement ignite = root.Element("Ignite");
            if (ignite == null) { return; }
            string material = (string)ignite.Attribute("material");
            if (!string.IsNullOrWhiteSpace(material)) { materialId = material.Trim().ToIdentifier(); }
            duration = ignite.GetAttributeFloat("duration", duration);
            damagePerSecond = ignite.GetAttributeFloat("damagepersecond", damagePerSecond);
            pileRadius = ignite.GetAttributeFloat("pileradius", pileRadius);
            warmthTarget = ignite.GetAttributeFloat("warmthtarget", warmthTarget);
            warmthRate = ignite.GetAttributeFloat("warmthrate", warmthRate);
            foreach (XElement source in ignite.Elements("Source")) {
                string id = (string)source.Attribute("identifier");
                if (!string.IsNullOrWhiteSpace(id)) { igniters.Add(id.Trim().ToIdentifier()); }
            }
        }

        public static void OnItemDamaged(Item item, Character attacker) {
            if (!ColdPlugin.Simulating || igniters.Count == 0 || item == null || item.Removed || attacker == null) { return; }
            if (burning.ContainsKey(item) || !IsFlammable(item) || !Wielding(attacker)) { return; }
            Ignite(item);
        }

        private static bool Wielding(Character attacker) {
            Item held = attacker.Inventory?.GetItemInLimbSlot(InvSlotType.RightHand) ??
                        attacker.Inventory?.GetItemInLimbSlot(InvSlotType.LeftHand);
            if (held == null || !igniters.Contains(held.Prefab.Identifier)) { return false; }
            LightComponent light = held.GetComponent<LightComponent>();
            return light != null && light.IsOn && light.IsActive;
        }

        private static void Ignite(Item item) {
            burning[item] = duration;
            Hull hull = item.CurrentHull;
            if (hull != null) { ThermalSourceRegistry.Set(hull, item, item.Prefab?.Name.Value ?? "burning", warmthTarget, warmthRate); }
        }

        private static void Extinguish(Item item) {
            Hull hull = item.Removed ? null : item.CurrentHull;
            if (hull != null) { ThermalSourceRegistry.Clear(hull, item); }
        }

        private static bool IsFlammable(Item item) {
            ItemPrefab prefab = item.Prefab;
            if (prefab == null) { return false; }
            if (flammable.TryGetValue(prefab, out bool cached)) { return cached; }
            bool result = item.GetComponent<Wearable>() != null && UsesMaterial(prefab.ConfigElement?.Element);
            flammable[prefab] = result;
            return result;
        }

        private static bool UsesMaterial(XElement root) {
            if (root == null || materialId.IsEmpty) { return false; }
            foreach (XElement child in root.Elements()) {
                string name = child.Name.LocalName;
                if (!name.Equals("Fabricate", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("Deconstruct", StringComparison.OrdinalIgnoreCase)) { continue; }
                foreach (XElement required in child.Elements()) {
                    string id = (string)required.Attribute("identifier");
                    if (!string.IsNullOrWhiteSpace(id) && id.Trim().ToIdentifier() == materialId) { return true; }
                }
            }
            return false;
        }

        public static void Update(float deltaTime) {
            if (burning.Count == 0) { return; }
            timer += deltaTime;
            if (timer < 1.0f) { return; }
            float step = timer;
            timer = 0.0f;

            stale.Clear();
            updates.Clear();
            foreach (KeyValuePair<Item, float> kv in burning) {
                Item item = kv.Key;
                if (item.Removed) { Extinguish(item); stale.Add(item); continue; }
                item.Condition -= damagePerSecond * step;
                float remaining = kv.Value - step;
                if (remaining <= 0.0f || item.Condition <= 0.0f) { Extinguish(item); stale.Add(item); continue; }
                updates.Add(new KeyValuePair<Item, float>(item, remaining));
                SpreadToPile(item);
            }
            for (int i = 0; i < stale.Count; i++) { burning.Remove(stale[i]); }
            for (int i = 0; i < updates.Count; i++) { burning[updates[i].Key] = updates[i].Value; }
        }

        private static void SpreadToPile(Item source) {
            Hull hull = source.CurrentHull;
            List<Item> items = Item.ItemList;
            for (int i = 0; i < items.Count; i++) {
                Item other = items[i];
                if (other == source || other.Removed || burning.ContainsKey(other) || other.CurrentHull != hull) { continue; }
                if (!IsFlammable(other)) { continue; }
                if (Vector2.DistanceSquared(other.WorldPosition, source.WorldPosition) > pileRadius * pileRadius) { continue; }
                Ignite(other);
            }
        }

        public static bool IsBurning(Item item) => item != null && burning.ContainsKey(item);

        public static void Reset() {
            foreach (Item item in burning.Keys) { Extinguish(item); }
            burning.Clear();
        }
    }
}
