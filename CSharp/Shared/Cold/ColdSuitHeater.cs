using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;

namespace PrimaryPackage.Cold {
    public static class ColdSuitHeater {
        private static readonly Dictionary<Character, float> bonus = new Dictionary<Character, float>();
        private static readonly List<Character> stale = new List<Character>();
        private static readonly HashSet<Identifier> suitTags = new HashSet<Identifier> { "diving".ToIdentifier() };
        private static readonly Identifier batteryTag = "mobilebattery".ToIdentifier();

        public static bool Enabled = true;
        public static float ScanInterval = 1.0f;
        public static float BatteryDrainPerSecond = 0.3f;
        public static float InsulationBonus = 10.0f;
        public static float BagBonus = 7.0f;
        public static float HandBonus = 4.0f;
        public static Identifier HeaterId = "pp_compactheater".ToIdentifier();

        private static float timer;

        public static float Bonus(Character character) =>
            character != null && bonus.TryGetValue(character, out float value) ? value : 0.0f;

        public static void Load(XElement root) {
            XElement heater = root.Element("SuitHeater");
            if (heater == null) { return; }
            Enabled = (bool?)heater.Attribute("enabled") ?? Enabled;
            ScanInterval = heater.GetAttributeFloat("scaninterval", ScanInterval);
            BatteryDrainPerSecond = heater.GetAttributeFloat("batterydrain", BatteryDrainPerSecond);
            InsulationBonus = heater.GetAttributeFloat("insulationbonus", InsulationBonus);
            BagBonus = heater.GetAttributeFloat("bagbonus", BagBonus);
            HandBonus = heater.GetAttributeFloat("handbonus", HandBonus);
            string id = (string)heater.Attribute("heateritem");
            if (!string.IsNullOrWhiteSpace(id)) { HeaterId = id.Trim().ToIdentifier(); }
            string tags = (string)heater.Attribute("suittags");
            if (string.IsNullOrWhiteSpace(tags)) { return; }
            suitTags.Clear();
            foreach (string tag in tags.Split(',')) {
                string trimmed = tag.Trim();
                if (trimmed.Length > 0) { suitTags.Add(trimmed.ToIdentifier()); }
            }
        }

        public static void Reset() {
            bonus.Clear();
            timer = 0.0f;
        }

        public static void Update(float deltaTime) {
            if (!Enabled) { return; }
            timer += deltaTime;
            if (timer < ScanInterval) { return; }
            float step = timer;
            timer = 0.0f;

            List<Character> all = Character.CharacterList;
            for (int i = 0; i < all.Count; i++) {
                Character character = all[i];
                if (!ColdSubject.Affects(character, null, out _)) { bonus.Remove(character); continue; }
                Item battery = FindHeat(character, out float amount);
                if (battery == null) { bonus.Remove(character); continue; }
                battery.Condition -= BatteryDrainPerSecond * step;
                bonus[character] = amount;
            }

            stale.Clear();
            foreach (var kv in bonus) { if (kv.Key.Removed || kv.Key.IsDead) { stale.Add(kv.Key); } }
            for (int i = 0; i < stale.Count; i++) { bonus.Remove(stale[i]); }
        }

        private static Item FindHeat(Character character, out float amount) {
            amount = 0.0f;
            CharacterInventory inventory = character.Inventory;
            if (inventory == null) { return null; }

            Item suit = inventory.GetItemInLimbSlot(InvSlotType.OuterClothes);
            if (suit is { Removed: false } && HasSuitTag(suit)) {
                Item internalCell = FindCharged(suit, 0);
                if (internalCell != null) { amount = InsulationBonus; return internalCell; }
            }
            Item cell = HeaterCell(inventory.GetItemInLimbSlot(InvSlotType.Bag));
            if (cell != null) { amount = BagBonus; return cell; }
            cell = HeaterCell(inventory.GetItemInLimbSlot(InvSlotType.RightHand))
                   ?? HeaterCell(inventory.GetItemInLimbSlot(InvSlotType.LeftHand));
            if (cell != null) { amount = HandBonus; return cell; }
            return null;
        }

        private static bool HasSuitTag(Item suit) {
            foreach (Identifier tag in suitTags) { if (suit.HasTag(tag)) { return true; } }
            return false;
        }

        private static Item HeaterCell(Item item) =>
            item is { Removed: false } && item.Prefab.Identifier == HeaterId ? FindCharged(item, 0) : null;

        private static Item FindCharged(Item container, int depth) {
            if (depth > 3) { return null; }
            foreach (Item contained in container.ContainedItems) {
                if (contained.Removed) { continue; }
                if (contained.HasTag(batteryTag) && contained.ConditionPercentage > 0.0f) { return contained; }
                Item nested = FindCharged(contained, depth + 1);
                if (nested != null) { return nested; }
            }
            return null;
        }
    }
}
