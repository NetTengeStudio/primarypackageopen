using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace PrimaryPackage.Cold {
    public static class ColdErcHeat {
        private struct Definition {
            public Identifier Id;
            public object Source;
            public float Target;
            public float Rate;
            public int Stack;
        }

        private readonly struct Candidate {
            public readonly Item Item;
            public readonly int DefinitionIndex;
            public readonly int ListIndex;

            public Candidate(Item item, int definitionIndex, int listIndex) {
                Item = item;
                DefinitionIndex = definitionIndex;
                ListIndex = listIndex;
            }
        }

        private static readonly List<Definition> definitions = new List<Definition>();
        private static readonly Dictionary<Identifier, int> lookup = new Dictionary<Identifier, int>();
        private static readonly List<Candidate> candidates = new List<Candidate>();
        private static readonly AccessTools.FieldRef<List<Item>, int> itemListVersion = CreateVersionReader();
        private static bool indexValid;
        private static int indexedVersion;
        private static Dictionary<Hull, float>[] counts = System.Array.Empty<Dictionary<Hull, float>>();
        private static HashSet<(Hull Hull, object Source)> previouslyActive = new HashSet<(Hull Hull, object Source)>();
        private static HashSet<(Hull Hull, object Source)> stillActive = new HashSet<(Hull Hull, object Source)>();
        private static float scanInterval = 2.0f;
        private static float timer;

        public static void Load(XElement root) {
            definitions.Clear();
            lookup.Clear();
            Reset();
            XElement erc = root.Element("ErcHeat");
            if (erc == null) { return; }
            scanInterval = erc.GetAttributeFloat("scaninterval", scanInterval);
            foreach (XElement source in erc.Elements("Source")) {
                string id = (string)source.Attribute("identifier");
                if (string.IsNullOrWhiteSpace(id)) { continue; }
                Identifier identifier = id.Trim().ToIdentifier();
                lookup[identifier] = definitions.Count;
                definitions.Add(new Definition {
                    Id = identifier,
                    Source = identifier,
                    Target = source.GetAttributeFloat("target", 40.0f),
                    Rate = source.GetAttributeFloat("rate", 0.1f),
                    Stack = System.Math.Max(1, source.GetAttributeInt("stack", 1))
                });
            }
            counts = new Dictionary<Hull, float>[definitions.Count];
            for (int i = 0; i < counts.Length; i++) { counts[i] = new Dictionary<Hull, float>(); }
        }

        public static void Reset() {
            foreach (var key in previouslyActive) { ThermalSourceRegistry.Clear(key.Hull, key.Source); }
            previouslyActive.Clear();
            stillActive.Clear();
            for (int i = 0; i < counts.Length; i++) { counts[i].Clear(); }
            candidates.Clear();
            indexValid = false;
        }

        public static void Update(float deltaTime) {
            if (definitions.Count == 0) { return; }
            timer += deltaTime;
            if (timer < scanInterval) { return; }
            timer = 0.0f;

            for (int i = 0; i < counts.Length; i++) { counts[i].Clear(); }
            List<Item> items = Item.ItemList;
            if (itemListVersion == null || !RefreshCandidates(items)) {
                CountItems(items, 0);
            } else {
                for (int i = 0; i < candidates.Count; i++) {
                    Candidate candidate = candidates[i];
                    if (!candidate.Item.Removed) { CountItem(candidate.Item, candidate.DefinitionIndex); }
                    if (itemListVersion(items) != indexedVersion) {
                        indexValid = false;
                        CountItems(items, candidate.ListIndex + 1);
                        break;
                    }
                }
            }

            stillActive.Clear();
            for (int d = 0; d < definitions.Count; d++) {
                Definition def = definitions[d];
                foreach (var kv in counts[d]) {
                    float factor = (kv.Value < def.Stack ? kv.Value : def.Stack) / def.Stack;
                    object tag = def.Source;
                    ThermalSourceRegistry.Set(kv.Key, tag, def.Id.Value, def.Target, def.Rate * factor);
                    stillActive.Add((kv.Key, tag));
                }
            }
            foreach (var key in previouslyActive) {
                if (!stillActive.Contains(key)) { ThermalSourceRegistry.Clear(key.Hull, key.Source); }
            }
            (previouslyActive, stillActive) = (stillActive, previouslyActive);
            stillActive.Clear();
        }

        private static AccessTools.FieldRef<List<Item>, int> CreateVersionReader() {
            try { return AccessTools.FieldRefAccess<List<Item>, int>("_version"); }
            catch (Exception e) {
                DebugConsole.ThrowError("pp_cold: cannot access item list version; heat sources will use a full scan", e);
                return null;
            }
        }

        private static bool RefreshCandidates(List<Item> items) {
            int version = itemListVersion(items);
            if (indexValid && version == indexedVersion) { return true; }
            int count = items.Count;
            candidates.Clear();
            for (int i = 0; i < count; i++) {
                Item item = items[i];
                if (lookup.TryGetValue(item.Prefab.Identifier, out int definition)) {
                    candidates.Add(new Candidate(item, definition, i));
                }
            }
            if (itemListVersion(items) != version) { indexValid = false; candidates.Clear(); return false; }
            indexedVersion = version;
            indexValid = true;
            return true;
        }

        private static void CountItems(List<Item> items, int start) {
            for (int i = start; i < items.Count; i++) {
                Item item = items[i];
                if (!item.Removed && lookup.TryGetValue(item.Prefab.Identifier, out int index)) { CountItem(item, index); }
            }
        }

        private static void CountItem(Item item, int index) {
            Hull hull = item.CurrentHull;
            if (hull == null || !IsLit(item)) { return; }
            Dictionary<Hull, float> bucket = counts[index];
            bucket.TryGetValue(hull, out float n);
            bucket[hull] = n + 1.0f;
        }

        private static bool IsLit(Item item) {
            LightComponent light = item.GetComponent<LightComponent>();
            return light != null && light.IsOn && light.IsActive;
        }
    }
}
