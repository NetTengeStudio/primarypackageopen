using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;

namespace PrimaryPackage.Cold {
    public static class ColdStasis {
        private static AfflictionPrefab stasisPrefab;
        private static bool checkedPrefab;
        private static float timer;

        public static bool Enabled = true;
        public static Identifier StasisAffliction = "stasis".ToIdentifier();
        public static Identifier StarterAffliction = "pp_stasis_starter".ToIdentifier();
        public static Identifier BagTag = "pp_stasisbag".ToIdentifier();
        public static float TriggerTemperature = 2.0f;
        public static float HoldRate = 4.0f;
        public static float StarterDecay = 0.15f;
        public static float Interval = 1.0f;

        public static int StasisSlot = -1;
        public static int StarterSlot = -1;

        public static bool Held(Character character) =>
            Enabled && ColdSnapshot.Strength(character, StasisSlot) > 0.0f;

        private static AfflictionPrefab StasisPrefab {
            get {
                if (checkedPrefab) { return stasisPrefab; }
                checkedPrefab = true;
                AfflictionPrefab.Prefabs.TryGet(StasisAffliction, out stasisPrefab);
                return stasisPrefab;
            }
        }

        public static void Load(XElement root) {
            XElement e = root.Element("Stasis");
            if (e == null) { return; }
            Enabled = (bool?)e.Attribute("enabled") ?? Enabled;
            TriggerTemperature = e.GetAttributeFloat("triggertemperature", TriggerTemperature);
            HoldRate = e.GetAttributeFloat("holdrate", HoldRate);
            StarterDecay = e.GetAttributeFloat("starterdecay", StarterDecay);
            Interval = e.GetAttributeFloat("interval", Interval);
            string id = (string)e.Attribute("stasisaffliction");
            if (!string.IsNullOrWhiteSpace(id)) { StasisAffliction = id.Trim().ToIdentifier(); }
            string starter = (string)e.Attribute("starteraffliction");
            if (!string.IsNullOrWhiteSpace(starter)) { StarterAffliction = starter.Trim().ToIdentifier(); }
            string bag = (string)e.Attribute("bagtag");
            if (!string.IsNullOrWhiteSpace(bag)) { BagTag = bag.Trim().ToIdentifier(); }
        }

        public static void Reset() {
            checkedPrefab = false;
            stasisPrefab = null;
            timer = 0.0f;
        }

        public static void Update(float deltaTime) {
            if (!Enabled) { return; }
            timer += deltaTime;
            if (timer < Interval) { return; }
            float step = timer;
            timer = 0.0f;

            AfflictionPrefab stasis = StasisPrefab;
            if (stasis == null) { return; }

            List<Character> all = Character.CharacterList;
            for (int i = 0; i < all.Count; i++) {
                Character character = all[i];
                if (character.Removed || character.IsDead) { continue; }
                CharacterHealth health = character.CharacterHealth;
                if (health == null) { continue; }

                float starter = ColdSnapshot.Strength(character, StarterSlot);
                bool bagged = character.Inventory?.GetItemInLimbSlot(InvSlotType.OuterClothes)?.HasTag(BagTag) ?? false;
                if (starter <= 0.0f && !bagged) { continue; }
                if (ColdAffliction.TemperatureAround(character) > TriggerTemperature) { continue; }

                health.ApplyAffliction(ColdAffliction.TargetLimb(character), stasis.Instantiate(HoldRate * step));
                if (starter > 0.0f) { health.ReduceAfflictionOnAllLimbs(StarterAffliction, StarterDecay * step); }
            }
        }
    }
}
