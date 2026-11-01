using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using PrimaryPackage.Cold;
using PrimaryPackage.Core;

namespace PrimaryPackage.Thoughts {
    public sealed class ThoughtProbe {
        private const float FreshBoneWindow = 7.0f;
        private const float WokeWindow = 9.0f;
        private const float BoneStep = 4.0f;
        private const float AimRange = 1500.0f;
        private const float AimRangeSq = AimRange * AimRange;
        private const float AimCosine = 0.977f;
        private const float AimCosineSq = AimCosine * AimCosine;
        private const float FearRise = 2.6f;
        private const float FearFall = 0.3f;
        private const float CalmDrift = 0.022f;
        private const float ColdCritical = 0.72f;

        private enum TypeKind : byte {
            None = 0,
            Pain,
            Damage,
            Burn,
            Fracture
        }

        private enum AfflictionKind : byte {
            None = 0,
            Analgesia,
            Anesthesia,
            Opioid,
            SeverePain,
            SeverePainLite,
            ShockPain,
            Hunger,
            Fever,
            Concussion,
            Confusion,
            CerebralHypoxia,
            Stroke,
            Drunk,
            OxygenLow,
            Hypoxemia,
            Unconsciousness,
            AdrenalineRush,
            TraumaticShock,
            Tachycardia,
            FractureItem,
            DislocationItem,
            AmputationItem
        }

        private static readonly Dictionary<Identifier, TypeKind> TypeClassifier = BuildTypeClassifier();
        private static readonly Dictionary<Identifier, AfflictionKind> AfflictionClassifier = BuildAfflictionClassifier();
        private static readonly Dictionary<Identifier, bool> RangedPrefabs = new Dictionary<Identifier, bool>();

        private float boneMass;
        private float freshBoneTimer;
        private float wokeTimer;
        private float arousal;
        private float lastVitality = float.NaN;
        private float damageEcho;
        private bool wasUnconscious;

        public float Arousal => arousal;

        public void Reset() {
            boneMass = 0.0f;
            freshBoneTimer = 0.0f;
            wokeTimer = 0.0f;
            arousal = -0.2f;
            lastVitality = float.NaN;
            damageEcho = 0.0f;
            wasUnconscious = false;
        }

        public ThoughtSignals Sample(Character character, float dt) {
            CharacterHealth health = character.CharacterHealth;
            if (health == null) { return default; }

            float maxVitality = health.MaxVitality;
            if (maxVitality < 1.0f) { maxVitality = 100.0f; }
            float vitality = health.Vitality / maxVitality;
            if (vitality < 0.0f) { vitality = 0.0f; } else if (vitality > 1.0f) { vitality = 1.0f; }

            float analgesia = 0.0f, anesthesia = 0.0f, opioid = 0.0f;
            float severePain = 0.0f, severePainLite = 0.0f, shockPain = 0.0f;
            float painType = 0.0f, damageType = 0.0f, burnType = 0.0f, boneTotal = 0.0f;
            float hunger = 0.0f, fever = 0.0f;
            float concussion = 0.0f, confusion = 0.0f, cerebralHypoxia = 0.0f, stroke = 0.0f, drunk = 0.0f;
            float oxygenLow = 0.0f, hypoxemia = 0.0f, unconsciousAffliction = 0.0f;
            float coldStrength = 0.0f;
            var thermal = new ThoughtThermal();
            float adrenaline = 0.0f, traumaticShock = 0.0f, tachycardia = 0.0f;
            bool hasFracture = false, hasDislocation = false, hasAmputation = false;

            Identifier coldId = ColdConfig.AfflictionId;
            var afflictions = health.GetAllAfflictions();
            if (afflictions != null) {
                foreach (Affliction a in afflictions) {
                    if (a == null) { continue; }
                    var prefab = a.Prefab;
                    if (prefab == null) { continue; }
                    float str = a.Strength;
                    if (str < prefab.ActivationThreshold) { continue; }

                    if (TypeClassifier.TryGetValue(prefab.AfflictionType, out var tKind)) {
                        switch (tKind) {
                            case TypeKind.Pain: painType += str; break;
                            case TypeKind.Damage: damageType += str; break;
                            case TypeKind.Burn: burnType += str; break;
                            case TypeKind.Fracture: boneTotal += str; break;
                        }
                    }

                    Identifier id = prefab.Identifier;
                    thermal.Include(id.Value, str);
                    if (id == coldId) {
                        coldStrength += str;
                    } else if (AfflictionClassifier.TryGetValue(id, out var aKind)) {
                        switch (aKind) {
                            case AfflictionKind.Analgesia: analgesia += str; break;
                            case AfflictionKind.Anesthesia: anesthesia += str; break;
                            case AfflictionKind.Opioid: opioid += str; break;
                            case AfflictionKind.SeverePain: severePain += str; break;
                            case AfflictionKind.SeverePainLite: severePainLite += str; break;
                            case AfflictionKind.ShockPain: shockPain += str; break;
                            case AfflictionKind.Hunger: hunger += str; break;
                            case AfflictionKind.Fever: fever += str; break;
                            case AfflictionKind.Concussion: concussion += str; break;
                            case AfflictionKind.Confusion: confusion += str; break;
                            case AfflictionKind.CerebralHypoxia: cerebralHypoxia += str; break;
                            case AfflictionKind.Stroke: stroke += str; break;
                            case AfflictionKind.Drunk: drunk += str; break;
                            case AfflictionKind.OxygenLow: oxygenLow += str; break;
                            case AfflictionKind.Hypoxemia: hypoxemia += str; break;
                            case AfflictionKind.Unconsciousness: unconsciousAffliction += str; break;
                            case AfflictionKind.AdrenalineRush: adrenaline += str; break;
                            case AfflictionKind.TraumaticShock: traumaticShock += str; break;
                            case AfflictionKind.Tachycardia: tachycardia += str; break;
                            case AfflictionKind.FractureItem: if (str > 5.0f) { hasFracture = true; } break;
                            case AfflictionKind.DislocationItem: if (str > 5.0f) { hasDislocation = true; } break;
                            case AfflictionKind.AmputationItem: if (str > 5.0f) { hasAmputation = true; } break;
                        }
                    }
                }
            }

            float sedation = analgesia + anesthesia + opioid * 0.5f;
            float pain = painType * 2.2f + (severePain + severePainLite + shockPain) * 20.0f
                         + (damageType + burnType) * (30.0f / maxVitality);
            pain += thermal.Pain;
            if (sedation > 0.0f) { pain /= 1.0f + sedation * 0.45f; }

            float bloodloss = health.BloodlossAmount;

            float brain = concussion * 0.035f + confusion * 0.2f + cerebralHypoxia * 0.005f
                          + stroke * 0.016f + drunk * 0.0035f;
            if (brain > 1.0f) { brain = 1.0f; }

            ThoughtFlags flags = ThoughtFlags.None;

            if (boneTotal > 0.5f) {
                if (hasFracture) { flags |= ThoughtFlags.Fracture; }
                if (hasDislocation) { flags |= ThoughtFlags.Dislocation; }
            }
            if (boneTotal > boneMass + BoneStep) { freshBoneTimer = FreshBoneWindow; }
            boneMass = boneTotal;
            if (freshBoneTimer > 0.0f) {
                freshBoneTimer -= dt;
                if (freshBoneTimer > 0.0f) { flags |= ThoughtFlags.FreshBone; }
            }

            if (hasAmputation) { flags |= ThoughtFlags.Amputation; }

            bool unconscious = character.IsUnconscious || health.IsUnconscious || unconsciousAffliction > 0.5f;
            if (unconscious) {
                flags |= ThoughtFlags.Unconscious;
                wokeTimer = WokeWindow;
            } else if (wasUnconscious) {
                flags |= ThoughtFlags.JustWoke;
            } else if (wokeTimer > 0.0f) {
                wokeTimer -= dt;
                if (wokeTimer > 0.0f) { flags |= ThoughtFlags.JustWoke; }
            }
            wasUnconscious = unconscious;

            float suffocation = oxygenLow * 0.01f + hypoxemia * 0.012f;
            if (character.NeedsOxygen && character.Oxygen < 40.0f) { suffocation += 0.6f; }
            if (suffocation > 0.45f) { flags |= ThoughtFlags.Choking; }

            if (fever > 0.6f) { flags |= ThoughtFlags.Fever; }

            float maxCold = ColdConfig.MaxStrength;
            float cold = maxCold <= 0.0f ? 0.0f : coldStrength / maxCold;
            if (cold > 1.0f) { cold = 1.0f; }
            if (thermal.Present) {
                cold = thermal.Cold;
                if (thermal.Shivering) { flags |= ThoughtFlags.Shivering; }
                if (thermal.Hypothermic) { flags |= ThoughtFlags.Hypothermic; }
                if (thermal.Overheated) { flags |= ThoughtFlags.Fever; }
            } else {
                if (ColdAffliction.ShiverResistance(cold) > 0.05f) { flags |= ThoughtFlags.Shivering; }
                if (cold >= ColdConfig.ShiverFail) { flags |= ThoughtFlags.Hypothermic; }
            }

            if (adrenaline > 1.0f) { flags |= ThoughtFlags.Adrenaline; }
            if (sedation > 1.5f) { flags |= ThoughtFlags.Sedated; }

            bool aimed = IsAimedAt(character);
            if (aimed) { flags |= ThoughtFlags.Aimed; }

            if (float.IsNaN(lastVitality)) { lastVitality = vitality; }
            float drop = lastVitality - vitality;
            lastVitality = vitality;
            if (drop > 0.005f) { damageEcho += drop * 6.0f; }
            damageEcho -= damageEcho * Scalar.Decay(0.35f, dt);
            if (damageEcho > 1.0f) { damageEcho = 1.0f; }

            float threat = (1.0f - vitality) * 0.55f
                           + (cold > ColdCritical ? (cold - ColdCritical) * 1.6f : 0.0f)
                           + bloodloss * 0.006f
                           + pain * 0.004f
                           + suffocation * 0.5f
                           + damageEcho
                           + traumaticShock * 0.004f
                           + tachycardia * 0.002f;
            if (aimed) { threat += 0.9f; }
            if ((flags & ThoughtFlags.Adrenaline) != 0) { threat -= 0.25f; }
            if (threat > 1.0f) { threat = 1.0f; } else if (threat < 0.0f) { threat = 0.0f; }

            if (threat > 0.03f) {
                float rate = threat > arousal ? FearRise : FearFall;
                arousal += (threat - arousal) * Scalar.Decay(rate, dt);
            } else {
                arousal += (-1.0f - arousal) * Scalar.Decay(CalmDrift, dt);
            }

            return new ThoughtSignals(pain, bloodloss, hunger, brain, arousal, vitality, cold, flags);
        }

        private static bool IsAimedAt(Character self) {
            Vector2 target = self.WorldPosition;
            List<Character> all = Character.CharacterList;
            for (int i = 0; i < all.Count; i++) {
                Character other = all[i];
                if (other == self || other.Removed || other.IsDead || other.IsUnconscious || !other.IsHuman) { continue; }
                if (!other.IsKeyDown(InputType.Aim)) { continue; }

                Vector2 origin = other.WorldPosition;
                float dx = target.X - origin.X;
                if (MathF.Abs(dx) > AimRange) { continue; }
                float dy = target.Y - origin.Y;
                if (MathF.Abs(dy) > AimRange) { continue; }

                float distSq = dx * dx + dy * dy;
                if (distSq > AimRangeSq || distSq < 100.0f) { continue; }

                Vector2 cursor = other.CursorWorldPosition;
                float ax = cursor.X - origin.X;
                float ay = cursor.Y - origin.Y;
                float aimSq = ax * ax + ay * ay;
                if (aimSq < 100.0f) { continue; }

                float proj = dx * ax + dy * ay;
                if (proj <= 0.0f) { continue; }

                if (proj * proj <= AimCosineSq * (distSq * aimSq)) { continue; }

                if (!HoldsRangedWeapon(other)) { continue; }
                return true;
            }
            return false;
        }

        private static bool HoldsRangedWeapon(Character character) {
            CharacterInventory inventory = character.Inventory;
            if (inventory == null) { return false; }
            Item item = inventory.GetItemInLimbSlot(InvSlotType.RightHand)
                        ?? inventory.GetItemInLimbSlot(InvSlotType.LeftHand);
            if (item?.Prefab == null) { return false; }
            Identifier id = item.Prefab.Identifier;
            if (RangedPrefabs.TryGetValue(id, out bool ranged)) { return ranged; }
            ranged = item.GetComponent<RangedWeapon>() != null || item.GetComponent<Turret>() != null;
            RangedPrefabs[id] = ranged;
            return ranged;
        }

        private static Dictionary<Identifier, TypeKind> BuildTypeClassifier() {
            return new Dictionary<Identifier, TypeKind> {
                { "pain".ToIdentifier(), TypeKind.Pain },
                { "damage".ToIdentifier(), TypeKind.Damage },
                { "burn".ToIdentifier(), TypeKind.Burn },
                { "fractures".ToIdentifier(), TypeKind.Fracture }
            };
        }

        private static Dictionary<Identifier, AfflictionKind> BuildAfflictionClassifier() {
            var dict = new Dictionary<Identifier, AfflictionKind> {
                { "analgesia".ToIdentifier(), AfflictionKind.Analgesia },
                { "anesthesia".ToIdentifier(), AfflictionKind.Anesthesia },
                { "afopioid".ToIdentifier(), AfflictionKind.Opioid },
                { "severepain".ToIdentifier(), AfflictionKind.SeverePain },
                { "severepainlite".ToIdentifier(), AfflictionKind.SeverePainLite },
                { "shockpain".ToIdentifier(), AfflictionKind.ShockPain },
                { "hunger".ToIdentifier(), AfflictionKind.Hunger },
                { "fever".ToIdentifier(), AfflictionKind.Fever },
                { "concussion".ToIdentifier(), AfflictionKind.Concussion },
                { "sym_confusion".ToIdentifier(), AfflictionKind.Confusion },
                { "cerebralhypoxia".ToIdentifier(), AfflictionKind.CerebralHypoxia },
                { "stroke".ToIdentifier(), AfflictionKind.Stroke },
                { "drunk".ToIdentifier(), AfflictionKind.Drunk },
                { "oxygenlow".ToIdentifier(), AfflictionKind.OxygenLow },
                { "hypoxemia".ToIdentifier(), AfflictionKind.Hypoxemia },
                { "sym_unconsciousness".ToIdentifier(), AfflictionKind.Unconsciousness },
                { "adrenalinerush".ToIdentifier(), AfflictionKind.AdrenalineRush },
                { "traumaticshock".ToIdentifier(), AfflictionKind.TraumaticShock },
                { "tachycardia".ToIdentifier(), AfflictionKind.Tachycardia }
            };

            string[] fractures = {
                "ra_fracture", "la_fracture", "rl_fracture", "ll_fracture", "t_fracture", "h_fracture", "n_fracture"
            };
            for (int i = 0; i < fractures.Length; i++) {
                dict[fractures[i].ToIdentifier()] = AfflictionKind.FractureItem;
            }

            string[] dislocations = {
                "dislocation1", "dislocation2", "dislocation3", "dislocation4"
            };
            for (int i = 0; i < dislocations.Length; i++) {
                dict[dislocations[i].ToIdentifier()] = AfflictionKind.DislocationItem;
            }

            string[] amputations = {
                "sra_amputation", "sla_amputation", "srl_amputation", "sll_amputation",
                "tra_amputation", "tla_amputation", "trl_amputation", "tll_amputation"
            };
            for (int i = 0; i < amputations.Length; i++) {
                dict[amputations[i].ToIdentifier()] = AfflictionKind.AmputationItem;
            }

            return dict;
        }
    }
}
