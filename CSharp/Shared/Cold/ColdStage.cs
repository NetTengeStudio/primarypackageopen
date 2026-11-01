using System;
using System.Collections.Generic;
using Barotrauma;

namespace PrimaryPackage.Cold {
    public enum ColdStage { Normal, Chill, Shiver, Hypothermic, Critical }

    public static class ColdStages {
        private static AfflictionPrefab cachedPrefab;
        private static float[] bounds = Array.Empty<float>();
        private static readonly Dictionary<Character, ColdStage> last = new Dictionary<Character, ColdStage>();
        private static readonly List<Character> stale = new List<Character>();

        public static event Action<Character, ColdStage, ColdStage> Changed;

        public static float ThresholdOf(ColdStage stage) {
            EnsureCache();
            int index = (int)stage - 1;
            if (index < 0) { return 0.0f; }
            return bounds.Length > index ? bounds[index] / ColdConfig.MaxStrength : 0.75f;
        }

        public static float CriticalThreshold => ThresholdOf(ColdStage.Critical);

        private static void EnsureCache() {
            AfflictionPrefab prefab = ColdAffliction.Prefab;
            if (prefab == null || ReferenceEquals(prefab, cachedPrefab)) { return; }
            cachedPrefab = prefab;
            int count = 0;
            foreach (AfflictionPrefab.Effect effect in prefab.Effects) { count++; }
            bounds = new float[count];
            int i = 0;
            foreach (AfflictionPrefab.Effect effect in prefab.Effects) { bounds[i++] = effect.MinStrength; }
            Array.Sort(bounds);
        }

        public static ColdStage GetStage(float normalizedStrength) {
            EnsureCache();
            if (bounds.Length == 0 || normalizedStrength <= 0.0f) { return ColdStage.Normal; }
            float absolute = normalizedStrength * ColdConfig.MaxStrength;
            int stage = 0;
            for (int i = 0; i < bounds.Length; i++) {
                if (absolute > bounds[i]) { stage = i + 1; }
            }
            return (ColdStage)Math.Min(stage, (int)ColdStage.Critical);
        }

        public static void Track(Character character, float normalizedStrength) {
            ColdStage stage = GetStage(normalizedStrength);
            if (last.TryGetValue(character, out ColdStage previous)) {
                if (previous == stage) { return; }
            } else {
                previous = ColdStage.Normal;
            }
            last[character] = stage;
            Changed?.Invoke(character, previous, stage);
        }

        public static void Forget(Character character) => last.Remove(character);

        public static void Prune() {
            if (last.Count == 0) { return; }
            stale.Clear();
            foreach (var kv in last) { if (kv.Key.Removed || kv.Key.IsDead) { stale.Add(kv.Key); } }
            for (int i = 0; i < stale.Count; i++) { last.Remove(stale[i]); }
        }

        public static void Reset() {
            last.Clear();
            cachedPrefab = null;
            bounds = Array.Empty<float>();
        }
    }
}
