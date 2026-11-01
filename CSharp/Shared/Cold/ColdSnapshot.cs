using System;
using System.Collections.Generic;
using Barotrauma;

namespace PrimaryPackage.Cold {
    public static class ColdSnapshot {
        private sealed class Row {
            public float[] Values = Array.Empty<float>();
            public int Tick = -1;
        }

        private static readonly Dictionary<Identifier, int> slots = new Dictionary<Identifier, int>();
        private static readonly Dictionary<Character, Row> rows = new Dictionary<Character, Row>();
        private static readonly List<Character> stale = new List<Character>();
        private static int width;
        private static int tick;
        private static int pruneCounter;

        public static int Register(Identifier id) {
            if (id.IsEmpty) { return -1; }
            if (slots.TryGetValue(id, out int existing)) { return existing; }
            slots[id] = width;
            return width++;
        }

        public static void Reset() {
            rows.Clear();
            tick = 0;
            pruneCounter = 0;
        }

        public static void Advance() {
            tick++;
            if (++pruneCounter < 64) { return; }
            pruneCounter = 0;
            stale.Clear();
            foreach (var kv in rows) { if (kv.Key.Removed || kv.Key.IsDead) { stale.Add(kv.Key); } }
            for (int i = 0; i < stale.Count; i++) { rows.Remove(stale[i]); }
        }

        public static float Strength(Character character, int slot) {
            if (slot < 0 || slot >= width || character == null) { return 0.0f; }
            float[] values = Values(character);
            return values == null ? 0.0f : values[slot];
        }

        private static float[] Values(Character character) {
            CharacterHealth health = character.CharacterHealth;
            if (health == null) { return null; }
            if (!rows.TryGetValue(character, out Row row)) {
                row = new Row();
                rows[character] = row;
            }
            if (row.Tick == tick && row.Values.Length == width) { return row.Values; }
            if (row.Values.Length != width) { row.Values = new float[width]; }
            else { Array.Clear(row.Values, 0, width); }
            row.Tick = tick;
            foreach (Affliction affliction in health.GetAllAfflictions()) {
                AfflictionPrefab prefab = affliction.Prefab;
                if (prefab == null || affliction.Strength < prefab.ActivationThreshold) { continue; }
                if (slots.TryGetValue(prefab.Identifier, out int slot)) { row.Values[slot] += affliction.Strength; }
            }
            return row.Values;
        }
    }
}
