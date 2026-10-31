using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Cold {
    public static class ColdHyperthermia {
        private static readonly Affliction[] buffer = new Affliction[1];
        private static AfflictionPrefab prefab;
        private static float timer;

        public static bool Enabled;
        public static Identifier AfflictionId = "pp_hyperthermia".ToIdentifier();
        public static float HotComfort = 33.0f;
        public static float MaxStrength = 100.0f;
        public static float GainPerDegree = 0.016f;
        public static float WarmRecoveryPerDegree = 0.05f;
        public static float InsulationPenalty = 0.5f;
        public static float ApplyInterval = 1.0f;
        public static LimbType TargetLimb = LimbType.Torso;

        public static AfflictionPrefab Prefab {
            get {
                if (prefab != null && prefab.Identifier == AfflictionId) { return prefab; }
                AfflictionPrefab.Prefabs.TryGet(AfflictionId, out prefab);
                return prefab;
            }
        }

        public static int Slot = -1;

        public static float Strength(Character character) => ColdSnapshot.Strength(character, Slot);

        public static float Normalized(Character character) =>
            MaxStrength <= 0.0f ? 0.0f : Strength(character) / MaxStrength;

        public static void Load(XElement root) {
            XElement hyper = root.Element("Hyperthermia");
            if (hyper == null) { return; }
            Enabled = (bool?)hyper.Attribute("enabled") ?? Enabled;
            string id = (string)hyper.Attribute("identifier");
            if (!string.IsNullOrWhiteSpace(id)) { AfflictionId = id.ToIdentifier(); }
            HotComfort = hyper.GetAttributeFloat("hotcomfort", HotComfort);
            MaxStrength = hyper.GetAttributeFloat("maxstrength", MaxStrength);
            GainPerDegree = hyper.GetAttributeFloat("gainperdegree", GainPerDegree);
            WarmRecoveryPerDegree = hyper.GetAttributeFloat("recoveryperdegree", WarmRecoveryPerDegree);
            InsulationPenalty = hyper.GetAttributeFloat("insulationpenalty", InsulationPenalty);
            ApplyInterval = hyper.GetAttributeFloat("applyinterval", ApplyInterval);
            string limb = (string)hyper.Attribute("limb");
            if (!string.IsNullOrWhiteSpace(limb) && System.Enum.TryParse(limb, true, out LimbType parsed)) { TargetLimb = parsed; }
        }

        public static void Reset() => timer = 0.0f;

        public static void Update(float deltaTime) {
            if (!Enabled) { return; }
            timer += deltaTime;
            if (timer < ApplyInterval) { return; }
            float step = timer;
            timer = 0.0f;

            AfflictionPrefab hot = Prefab;
            if (hot == null) { return; }

            List<Character> all = Character.CharacterList;
            for (int i = 0; i < all.Count; i++) {
                Character character = all[i];
                if (!ColdSubject.Affects(character, hot, out CharacterHealth health)) { continue; }

                float temperature = ColdAffliction.TemperatureAround(character);
                float above = temperature + ColdAffliction.Insulation(character) * InsulationPenalty - HotComfort;
                if (above > 0.0f) {
                    float amount = above * GainPerDegree * step * ColdSubject.ScaleFactor;
                    Limb limb = character.AnimController?.GetLimb(TargetLimb) ?? character.AnimController?.MainLimb;
                    if (limb == null || limb.Removed) { continue; }
                    buffer[0] = hot.Instantiate(amount);
                    character.DamageLimb(limb.WorldPosition, limb, buffer, stun: 0.0f, playSound: false,
                        attackImpulse: Vector2.Zero, recalculateVitality: true);
                    continue;
                }

                float strength = Strength(character);
                if (strength <= 0.0f || WarmRecoveryPerDegree <= 0.0f) { continue; }
                health.ReduceAfflictionOnAllLimbs(AfflictionId, -above * WarmRecoveryPerDegree * step);
            }
        }
    }
}
