using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using PrimaryPackage.Thoughts;

namespace PrimaryPackage.Cold {
    public static class ColdAffliction {
        private static readonly Affliction[] buffer = new Affliction[1];
        private static readonly Dictionary<Character, float> wetness = new Dictionary<Character, float>();
        private static readonly List<Character> staleBuffer = new List<Character>();
        private static AfflictionPrefab prefab;
        private static float timer;

        public static AfflictionPrefab Prefab {
            get {
                if (prefab != null && prefab.Identifier == ColdConfig.AfflictionId) { return prefab; }
                AfflictionPrefab.Prefabs.TryGet(ColdConfig.AfflictionId, out prefab);
                return prefab;
            }
        }

        public static int Slot = -1;

        public static float Strength(Character character) => ColdSnapshot.Strength(character, Slot);

        public static float Normalized(Character character) {
            float max = ColdConfig.MaxStrength;
            return max <= 0.0f ? 0.0f : Strength(character) / max;
        }

        public static float TemperatureAround(Character character) {
            Hull hull = character.CurrentHull;
            if (hull != null) { return ColdField.Get(hull); }
            return ColdZoneComponent.TryGetOpenTemperature(character, out float open)
                ? open : ColdConfig.NoHullTemperature;
        }

        public static Limb TargetLimb(Character character) =>
            character?.AnimController?.GetLimb(ColdConfig.TargetLimb) ?? character?.AnimController?.MainLimb;

        public static float Resistance(Character character, out string source) =>
            ResistanceOn(TargetLimb(character), out source);

        public static float ResistanceOn(Limb limb, out string source) {
            source = null;
            AfflictionPrefab prefab = Prefab;
            if (prefab == null || limb == null) { return 1.0f; }
            Identifier id = prefab.Identifier;
            Identifier type = prefab.AfflictionType;
            float multiplier = 1.0f;
            for (int i = 0; i < limb.DamageModifiers.Count; i++) {
                DamageModifier modifier = limb.DamageModifiers[i];
                if (!modifier.MatchesAffliction(id, type)) { continue; }
                multiplier *= modifier.DamageMultiplier;
                source ??= "тело";
            }
            for (int i = 0; i < limb.WearingItems.Count; i++) {
                var wearable = limb.WearingItems[i]?.WearableComponent;
                if (wearable == null) { continue; }
                foreach (DamageModifier modifier in wearable.DamageModifiers) {
                    if (!modifier.MatchesAffliction(id, type)) { continue; }
                    multiplier *= modifier.DamageMultiplier;
                    source ??= wearable.Item?.Prefab?.Name?.Value;
                }
            }
            return multiplier;
        }

        public static float InsulationFrom(float multiplier) =>
            multiplier >= 1.0f ? 0.0f : (1.0f - multiplier) * ColdConfig.InsulationScale;

        public static readonly List<(int Slot, float Scale)> Warming = new List<(int, float)>();

        public static float WarmthBonus(Character character) {
            if (character == null || Warming.Count == 0) { return 0.0f; }
            float total = 0.0f;
            for (int i = 0; i < Warming.Count; i++) {
                total += ColdSnapshot.Strength(character, Warming[i].Slot) * Warming[i].Scale;
            }
            return total;
        }

        public static float Insulation(Character character) =>
            InsulationFrom(Resistance(character, out _)) + ColdSuitHeater.Bonus(character) + WarmthBonus(character);

        public static float GainStep(float temperature, float insulation, float wet, float strength, float step) {
            float below = ColdConfig.Comfort - insulation - temperature;
            if (below <= 0.0f) { return 0.0f; }
            float amount = below * ColdConfig.GainPerDegree * step;
            amount *= 1.0f + wet * (ColdConfig.WetMultiplier - 1.0f);
            amount *= 1.0f - ShiverResistance(ColdConfig.MaxStrength <= 0.0f ? 0.0f : strength / ColdConfig.MaxStrength);
            amount *= ColdSubject.ScaleFactor;
            return amount > 0.0f ? amount : 0.0f;
        }

        public static float ShiverResistance(float strength) {
            float peak = ColdConfig.ShiverPeak;
            float fail = ColdConfig.ShiverFail;
            if (ColdConfig.ShiverResist <= 0.0f || strength >= fail) { return 0.0f; }
            float shape = strength <= peak
                ? (peak <= 0.0f ? 1.0f : strength / peak)
                : 1.0f - (strength - peak) / (fail - peak);
            return ColdConfig.ShiverResist * PrimaryPackage.Core.Scalar.Smooth(PrimaryPackage.Core.Scalar.Clamp01(shape));
        }

        public static float Wetness(Character character) =>
            character != null && wetness.TryGetValue(character, out float value) ? value : 0.0f;

        public static void ReduceWetness(Character character, float amount) {
            if (character == null || amount <= 0.0f) { return; }
            float next = Wetness(character) - amount;
            if (next <= 0.0f) { wetness.Remove(character); } else { wetness[character] = next; }
        }

        public static bool IsTarget(AfflictionPrefab prefab, Character character) {
            Identifier[] species = prefab.TargetSpecies;
            if (species == null || species.Length == 0) { return true; }
            Identifier name = character.SpeciesName;
            for (int i = 0; i < species.Length; i++) {
                if (species[i] == name) { return true; }
            }
            return false;
        }

        private static float UpdateWetness(Character character, float temperature, float step) {
            bool submerged = character.AnimController != null && character.AnimController.InWater;
            float current = wetness.TryGetValue(character, out float stored) ? stored : 0.0f;
            if (submerged) {
                current += ColdConfig.WetRise * step;
            } else {
                float warmth = temperature > ColdConfig.Comfort ? 2.0f : 1.0f;
                current -= ColdConfig.WetDry * warmth * step;
            }
            if (current > 1.0f) { current = 1.0f; } else if (current < 0.0f) { current = 0.0f; }
            if (current <= 0.0f) {
                wetness.Remove(character);
                return 0.0f;
            }
            wetness[character] = current;
            return current;
        }

        public static void Reset() {
            wetness.Clear();
            timer = 0.0f;
        }

        public static void Update(float deltaTime) {
            timer += deltaTime;
            if (timer < ColdConfig.ApplyInterval) { return; }
            float step = timer;
            timer = 0.0f;
            ColdStages.Prune();
            if (ColdSubject.Disabled) { return; }

            if (wetness.Count > 0) {
                staleBuffer.Clear();
                foreach (var kv in wetness) {
                    if (kv.Key.Removed || kv.Key.IsDead) { staleBuffer.Add(kv.Key); }
                }
                for (int i = 0; i < staleBuffer.Count; i++) { wetness.Remove(staleBuffer[i]); }
            }

            AfflictionPrefab cold = Prefab;
            if (cold == null) { return; }

            List<Character> all = Character.CharacterList;
            for (int i = 0; i < all.Count; i++) {
                Character character = all[i];
                if (!ColdSubject.Affects(character, cold, out CharacterHealth health)) { continue; }

                float temperature = TemperatureAround(character);
                float insulation = Insulation(character);
                float wet = UpdateWetness(character, temperature, step);
                float strength = Strength(character);
                ColdStages.Track(character, ColdConfig.MaxStrength <= 0.0f ? 0.0f : strength / ColdConfig.MaxStrength);
                float amount = GainStep(temperature, insulation, wet, strength, step);
                if (amount > 0.0f) {
                    Limb limb = TargetLimb(character);
                    if (limb == null || limb.Removed) { continue; }
                    buffer[0] = cold.Instantiate(amount);
                    character.DamageLimb(limb.WorldPosition, limb, buffer, stun: 0.0f, playSound: false,
                        attackImpulse: Vector2.Zero, recalculateVitality: true);
                    continue;
                }

                float above = temperature + insulation - ColdConfig.Comfort;
                if (above <= 0.0f || strength <= 0.0f || ColdConfig.WarmRecoveryPerDegree <= 0.0f) { continue; }
                health.ReduceAfflictionOnAllLimbs(ColdConfig.AfflictionId, above * ColdConfig.WarmRecoveryPerDegree * step);
            }
        }
    }
}
