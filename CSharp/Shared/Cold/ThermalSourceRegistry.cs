using System.Collections.Generic;
using Barotrauma;

namespace PrimaryPackage.Cold {
    public static class ThermalSourceRegistry {
        public readonly struct Entry {
            public readonly Hull Hull;
            public readonly string Label;
            public readonly float Target;
            public readonly float Rate;

            public Entry(Hull hull, string label, float target, float rate) {
                Hull = hull;
                Label = label;
                Target = target;
                Rate = rate;
            }
        }

        private static readonly Dictionary<(Hull, object), Entry> entries = new Dictionary<(Hull, object), Entry>();
        private static float timer;

        public static int Count => entries.Count;
        public static Dictionary<(Hull, object), Entry>.ValueCollection All => entries.Values;

        public static void Set(Hull hull, object source, string label, float target, float rate) {
            if (hull == null || source == null) { return; }
            entries[(hull, source)] = new Entry(hull, label, target, rate);
        }

        public static void Clear(Hull hull, object source) => entries.Remove((hull, source));

        public static void Reset() {
            entries.Clear();
            timer = 0.0f;
        }

        public static void Update(float deltaTime) {
            if (entries.Count == 0) { return; }
            timer += deltaTime;
            if (timer < ColdConfig.TickInterval) { return; }
            float step = timer;
            timer = 0.0f;
            foreach (Entry entry in entries.Values) { ColdField.Nudge(entry.Hull, entry.Target, entry.Rate, step); }
        }
    }
}
