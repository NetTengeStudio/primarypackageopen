using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;

namespace PrimaryPackage.Cold {
    public static class ColdComplications {
        public enum Channel { Cold, Heat, Frostbite, Affliction }

        private readonly struct Rule {
            public readonly Identifier Id;
            public readonly Channel Source;
            public readonly float Min;
            public readonly float Max;
            public readonly float Rate;
            public readonly float Decay;
            public readonly float Cap;
            public readonly bool Extremity;
            public readonly LimbType Limb;
            public readonly Identifier Driver;
            public readonly float DriverMax;
            public readonly int Slot;
            public readonly int DriverSlot;

            public Rule(XElement e) {
                Id = ((string)e.Attribute("identifier") ?? string.Empty).Trim().ToIdentifier();
                Source = ((string)e.Attribute("source") ?? "cold").Trim().ToLowerInvariant() switch {
                    "heat" => Channel.Heat,
                    "frostbite" => Channel.Frostbite,
                    "affliction" => Channel.Affliction,
                    _ => Channel.Cold
                };
                Driver = ((string)e.Attribute("driver") ?? string.Empty).Trim().ToIdentifier();
                DriverMax = e.GetAttributeFloat("drivermax", 100.0f);
                Slot = ColdSnapshot.Register(Id);
                DriverSlot = ColdSnapshot.Register(Driver);
                Min = e.GetAttributeFloat("min", 0.0f);
                Max = e.GetAttributeFloat("max", 1.0f);
                Rate = e.GetAttributeFloat("rate", 1.0f);
                Decay = e.GetAttributeFloat("decay", 1.0f);
                Cap = e.GetAttributeFloat("cap", 100.0f);
                string limb = ((string)e.Attribute("limb") ?? "torso").Trim();
                Extremity = limb.Equals("extremities", System.StringComparison.OrdinalIgnoreCase);
                Limb = !Extremity && System.Enum.TryParse(limb, true, out LimbType parsed) ? parsed : LimbType.Torso;
            }
        }

        private static readonly LimbType[] extremities = { LimbType.LeftHand, LimbType.RightHand, LimbType.LeftFoot, LimbType.RightFoot };
        private static readonly List<Rule> rules = new List<Rule>();
        private static AfflictionPrefab[] prefabs = System.Array.Empty<AfflictionPrefab>();
        private static bool resolved;
        private static float timer;

        public static bool Enabled = true;
        public static float Interval = 1.0f;

        public static void Load(XElement root) {
            rules.Clear();
            resolved = false;
            XElement group = root.Element("Complications");
            if (group == null) { return; }
            Enabled = (bool?)group.Attribute("enabled") ?? Enabled;
            Interval = group.GetAttributeFloat("interval", Interval);
            foreach (XElement e in group.Elements("Complication")) {
                var rule = new Rule(e);
                if (!rule.Id.IsEmpty) { rules.Add(rule); }
            }
            prefabs = new AfflictionPrefab[rules.Count];
        }

        public static void Reset() {
            resolved = false;
            timer = 0.0f;
        }

        private static void Resolve() {
            resolved = true;
            for (int i = 0; i < rules.Count; i++) {
                AfflictionPrefab.Prefabs.TryGet(rules[i].Id, out AfflictionPrefab found);
                prefabs[i] = found;
                if (found == null) { DebugConsole.AddWarning("pp_cold: аффликшен осложнения " + rules[i].Id + " не найден."); }
            }
        }

        public static void Update(float deltaTime) {
            if (!Enabled || rules.Count == 0) { return; }
            timer += deltaTime;
            if (timer < Interval) { return; }
            float step = timer;
            timer = 0.0f;
            if (!resolved) { Resolve(); }

            List<Character> all = Character.CharacterList;
            for (int c = 0; c < all.Count; c++) {
                Character character = all[c];
                if (!ColdSubject.Affects(character, null, out CharacterHealth health)) { continue; }
                float cold = ColdAffliction.Normalized(character);
                float heat = ColdHyperthermia.Normalized(character);
                float frost = ColdFrostbite.MaxStrength <= 0.0f ? 0.0f : ColdFrostbite.Strength(character) / ColdFrostbite.MaxStrength;

                for (int r = 0; r < rules.Count; r++) {
                    AfflictionPrefab prefab = prefabs[r];
                    if (prefab == null || !ColdAffliction.IsTarget(prefab, character)) { continue; }
                    Rule rule = rules[r];
                    float driver = rule.Source switch {
                        Channel.Heat => heat,
                        Channel.Frostbite => frost,
                        Channel.Affliction => rule.DriverMax <= 0.0f || rule.DriverSlot < 0 ? 0.0f
                            : ColdSnapshot.Strength(character, rule.DriverSlot) / rule.DriverMax,
                        _ => cold
                    };
                    bool active = driver >= rule.Min && driver <= rule.Max;
                    Limb limb = rule.Extremity
                        ? character.AnimController?.GetLimb(extremities[Rand.Int(extremities.Length)])
                        : character.AnimController?.GetLimb(rule.Limb) ?? character.AnimController?.MainLimb;
                    if (limb == null || limb.Removed) { continue; }

                    if (active) {
                        float room = rule.Cap - ColdSnapshot.Strength(character, rule.Slot);
                        if (room <= 0.0f) { continue; }
                        float amount = rule.Rate * step * ColdSubject.ScaleFactor;
                        health.ApplyAffliction(limb, prefab.Instantiate(amount < room ? amount : room));
                    } else if (rule.Decay > 0.0f) {
                        health.ReduceAfflictionOnAllLimbs(rule.Id, rule.Decay * step);
                    }
                }
            }
        }
    }
}
