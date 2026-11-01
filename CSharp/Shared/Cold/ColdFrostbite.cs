using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;

namespace PrimaryPackage.Cold {
    public static class ColdFrostbite {
        private static readonly LimbType[] extremities = { LimbType.LeftHand, LimbType.RightHand, LimbType.LeftFoot, LimbType.RightFoot };
        private static AfflictionPrefab prefab;
        private static AfflictionPrefab gangrenePrefab;
        private static bool gangreneChecked;
        private static float timer;

        public static bool Enabled = true;
        public static Identifier AfflictionId = "pp_frostbite".ToIdentifier();
        public static float MaxStrength = 100.0f;
        public static float RateHypothermic = 0.35f;
        public static float RateCritical = 1.0f;
        public static float Recovery = 0.6f;
        public static float NecrosisAt = 75.0f;
        public static float GangreneRate = 0.25f;
        public static float InsulationShield = 0.06f;
        public static float ThawInjury = 3.0f;
        public static float ThawSafeRate = 0.9f;
        public static Identifier GangreneAffliction = "gangrene".ToIdentifier();
        public static Identifier ThawInjuryAffliction = "burn".ToIdentifier();

        public static AfflictionPrefab Prefab {
            get {
                if (prefab != null && prefab.Identifier == AfflictionId) { return prefab; }
                AfflictionPrefab.Prefabs.TryGet(AfflictionId, out prefab);
                return prefab;
            }
        }

        private static AfflictionPrefab GangrenePrefab {
            get {
                if (gangreneChecked) { return gangrenePrefab; }
                gangreneChecked = true;
                AfflictionPrefab.Prefabs.TryGet(GangreneAffliction, out gangrenePrefab);
                return gangrenePrefab;
            }
        }

        public static float Strength(Character character) {
            CharacterHealth health = character?.CharacterHealth;
            if (health == null) { return 0.0f; }
            float worst = 0.0f;
            for (int i = 0; i < extremities.Length; i++) {
                Limb limb = character.AnimController?.GetLimb(extremities[i]);
                if (limb == null) { continue; }
                float value = health.GetAffliction(AfflictionId, limb)?.Strength ?? 0.0f;
                if (value > worst) { worst = value; }
            }
            return worst;
        }

        public static void Load(XElement root) {
            XElement frostbite = root.Element("Frostbite");
            if (frostbite == null) { return; }
            Enabled = (bool?)frostbite.Attribute("enabled") ?? Enabled;
            string id = (string)frostbite.Attribute("identifier");
            if (!string.IsNullOrWhiteSpace(id)) { AfflictionId = id.Trim().ToIdentifier(); }
            MaxStrength = frostbite.GetAttributeFloat("maxstrength", MaxStrength);
            RateHypothermic = frostbite.GetAttributeFloat("ratehypothermic", RateHypothermic);
            RateCritical = frostbite.GetAttributeFloat("ratecritical", RateCritical);
            Recovery = frostbite.GetAttributeFloat("recovery", Recovery);
            NecrosisAt = frostbite.GetAttributeFloat("necrosisat", NecrosisAt);
            GangreneRate = frostbite.GetAttributeFloat("gangrenerate", GangreneRate);
            InsulationShield = frostbite.GetAttributeFloat("insulationshield", InsulationShield);
            ThawInjury = frostbite.GetAttributeFloat("thawinjury", ThawInjury);
            ThawSafeRate = frostbite.GetAttributeFloat("thawsaferate", ThawSafeRate);
            string gangrene = (string)frostbite.Attribute("gangreneaffliction");
            if (!string.IsNullOrWhiteSpace(gangrene)) { GangreneAffliction = gangrene.Trim().ToIdentifier(); }
            string thaw = (string)frostbite.Attribute("thawinjuryaffliction");
            if (!string.IsNullOrWhiteSpace(thaw)) { ThawInjuryAffliction = thaw.Trim().ToIdentifier(); }
        }

        public static void Reset() {
            gangreneChecked = false;
            gangrenePrefab = null;
            prefab = null;
            timer = 0.0f;
        }

        public static void Update(float deltaTime) {
            if (!Enabled) { return; }
            timer += deltaTime;
            if (timer < ColdConfig.ApplyInterval) { return; }
            float step = timer;
            timer = 0.0f;

            AfflictionPrefab frostbite = Prefab;
            if (frostbite == null) { return; }
            AfflictionPrefab gangrene = GangrenePrefab;

            List<Character> all = Character.CharacterList;
            for (int i = 0; i < all.Count; i++) {
                Character character = all[i];
                if (!ColdSubject.Affects(character, frostbite, out CharacterHealth health)) { continue; }

                float baseRate = ColdStages.GetStage(ColdAffliction.Normalized(character)) switch {
                    ColdStage.Critical => RateCritical,
                    ColdStage.Hypothermic => RateHypothermic,
                    _ => -Recovery
                };
                bool warming = baseRate < 0.0f;
                for (int e = 0; e < extremities.Length; e++) {
                    Limb limb = character.AnimController?.GetLimb(extremities[e]);
                    if (limb == null || limb.Removed || limb.IsSevered) { continue; }
                    float current = health.GetAffliction(AfflictionId, limb)?.Strength ?? 0.0f;
                    if (current <= 0.0f && warming) { continue; }

                    float rate = warming ? baseRate
                        : baseRate / (1.0f + ColdAffliction.InsulationFrom(ColdAffliction.ResistanceOn(limb, out _)) * InsulationShield);
                    float floor = current >= NecrosisAt ? NecrosisAt : 0.0f;
                    float target = current + rate * step * ColdSubject.ScaleFactor;
                    if (target > MaxStrength) { target = MaxStrength; } else if (target < floor) { target = floor; }
                    float delta = target - current;

                    if (delta > 0.0f) {
                        health.ApplyAffliction(limb, frostbite.Instantiate(delta));
                    } else if (delta < 0.0f) {
                        health.ReduceAfflictionOnLimb(limb, AfflictionId, -delta);
                        float thawSpeed = -delta / step;
                        if (current >= NecrosisAt * 0.34f && thawSpeed > ThawSafeRate) {
                            ApplyThawInjury(health, limb, (thawSpeed - ThawSafeRate) * ThawInjury * step);
                        }
                    }

                    if (target < NecrosisAt || gangrene == null || !ColdAffliction.IsTarget(gangrene, character)) { continue; }
                    health.ApplyAffliction(limb, gangrene.Instantiate(GangreneRate * step));
                }
            }
        }

        private static void ApplyThawInjury(CharacterHealth health, Limb limb, float amount) {
            if (amount <= 0.0f || !AfflictionPrefab.Prefabs.TryGet(ThawInjuryAffliction, out AfflictionPrefab injury)) { return; }
            health.ApplyAffliction(limb, injury.Instantiate(amount));
        }
    }
}
