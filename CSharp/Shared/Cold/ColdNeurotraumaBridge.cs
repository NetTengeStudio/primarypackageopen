using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;

namespace PrimaryPackage.Cold {
    public static class ColdNeurotraumaBridge {
        private struct Track {
            public float Baseline;
            public float Cooldown;
            public bool InWater;
        }

        private static readonly Dictionary<Character, Track> tracked = new Dictionary<Character, Track>();
        private static readonly List<Character> stale = new List<Character>();
        private static AfflictionPrefab shockPrefab;
        private static bool shockChecked;
        private static bool? present;
        private static float timer;

        public static bool Present {
            get {
                if (present.HasValue) { return present.Value; }
                present = ContentPackageManager.EnabledPackages.All.Any(p =>
                    p.Name.IndexOf("Neurotrauma", StringComparison.OrdinalIgnoreCase) >= 0);
                return present.Value;
            }
        }

        private static AfflictionPrefab ShockPrefab {
            get {
                if (shockChecked) { return shockPrefab; }
                shockChecked = true;
                AfflictionPrefab.Prefabs.TryGet(ColdConfig.NtShockAffliction, out shockPrefab);
                return shockPrefab;
            }
        }

        public static void Reset() {
            tracked.Clear();
            present = null;
            shockChecked = false;
            shockPrefab = null;
            timer = 0.0f;
        }

        public static void Update(float deltaTime) {
            if (!ColdConfig.NtEnabled || !Present) { return; }
            timer += deltaTime;
            if (timer < ColdConfig.NtInterval) { return; }
            float step = timer;
            timer = 0.0f;

            AfflictionPrefab shock = ShockPrefab;
            if (shock == null) { return; }

            List<Character> all = Character.CharacterList;
            for (int i = 0; i < all.Count; i++) {
                Character character = all[i];
                if (!ColdSubject.Affects(character, shock, out CharacterHealth health)) { tracked.Remove(character); continue; }

                float ambient = ColdAffliction.TemperatureAround(character) + ColdAffliction.Insulation(character);
                bool inWater = character.AnimController is { InWater: true };
                bool known = tracked.TryGetValue(character, out Track track);
                if (!known) { track = new Track { Baseline = ambient }; }
                track.Cooldown -= step;

                float amount = 0.0f;
                if (known && track.Cooldown <= 0.0f) {
                    if (inWater && !track.InWater) {
                        float chill = ColdConfig.Comfort - ColdConfig.NtWaterMargin - ColdAffliction.TemperatureAround(character);
                        if (chill > 0.0f) { amount = chill * ColdConfig.NtShockPerDegree; }
                    } else if (!inWater && !track.InWater) {
                        float swing = Math.Abs(ambient - track.Baseline);
                        bool crossed = (track.Baseline - ColdConfig.Comfort) * (ambient - ColdConfig.Comfort) < 0.0f;
                        if (crossed && swing > ColdConfig.NtShockMargin) {
                            amount = (swing - ColdConfig.NtShockMargin) * ColdConfig.NtShockPerDegree;
                        }
                    }
                }

                track.InWater = inWater;
                track.Baseline += (ambient - track.Baseline) * Response(ColdConfig.NtShockAdapt, step);
                if (amount > 0.0f) {
                    track.Cooldown = ColdConfig.NtShockCooldown;
                    if (amount > ColdConfig.NtShockCap) { amount = ColdConfig.NtShockCap; }
                    health.ApplyAffliction(ColdAffliction.TargetLimb(character), shock.Instantiate(amount));
                }
                tracked[character] = track;
            }

            stale.Clear();
            foreach (var kv in tracked) { if (kv.Key.Removed || kv.Key.IsDead) { stale.Add(kv.Key); } }
            for (int i = 0; i < stale.Count; i++) { tracked.Remove(stale[i]); }
        }

        private static float Response(float rate, float step) {
            float x = rate * step;
            return x <= 0.0f ? 0.0f : x >= 6.0f ? 1.0f : 1.0f - (float)Math.Exp(-x);
        }
    }
}
