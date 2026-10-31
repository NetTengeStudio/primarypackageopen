using System.Collections.Generic;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;

namespace PrimaryPackage.Cold {
    public static class ColdReactorHeat {
        private static readonly List<Reactor> reactors = new List<Reactor>();
        private static HashSet<(Hull, object)> active = new HashSet<(Hull, object)>();
        private static HashSet<(Hull, object)> scratch = new HashSet<(Hull, object)>();
        private static float scanTimer;
        private static float rescanTimer = float.MaxValue;

        public static bool Enabled = true;
        public static bool AccountForRods = true;
        public static float RodBase = 25.0f;
        public static float RodStep = 20.0f;
        public static float MaxTemperature = 60.0f;
        public static float ScanInterval = 2.0f;
        public static float RescanInterval = 20.0f;
        public static float Target = 46.0f;
        public static float RatePerDegree = 0.0025f;

        public static int Count => reactors.Count;

        public static void Load(XElement root) {
            XElement e = root.Element("Reactor");
            if (e == null) { return; }
            Enabled = (bool?)e.Attribute("enabled") ?? Enabled;
            AccountForRods = (bool?)e.Attribute("accountforrods") ?? AccountForRods;
            RodBase = e.GetAttributeFloat("rodbase", RodBase);
            RodStep = e.GetAttributeFloat("rodstep", RodStep);
            MaxTemperature = e.GetAttributeFloat("maxtemperature", MaxTemperature);
            ScanInterval = e.GetAttributeFloat("scaninterval", ScanInterval);
            RescanInterval = e.GetAttributeFloat("rescaninterval", RescanInterval);
            Target = e.GetAttributeFloat("target", Target);
            RatePerDegree = e.GetAttributeFloat("rate", RatePerDegree);
        }

        public static void Reset() {
            foreach ((Hull hull, object source) in active) { ThermalSourceRegistry.Clear(hull, source); }
            active.Clear();
            scratch.Clear();
            reactors.Clear();
            scanTimer = 0.0f;
            rescanTimer = float.MaxValue;
        }

        public static void Update(float deltaTime) {
            if (!Enabled) { return; }
            rescanTimer += deltaTime;
            scanTimer += deltaTime;
            if (scanTimer < ScanInterval) { return; }
            scanTimer = 0.0f;
            if (rescanTimer >= RescanInterval) { Rescan(); }

            scratch.Clear();
            for (int i = reactors.Count - 1; i >= 0; i--) {
                Reactor reactor = reactors[i];
                Item item = reactor.Item;
                if (item == null || item.Removed) { reactors.RemoveAt(i); continue; }
                Hull hull = item.CurrentHull;
                if (hull == null) { continue; }
                float over = reactor.Temperature - Threshold(item);
                if (over <= 0.0f) { continue; }
                ThermalSourceRegistry.Set(hull, reactor, "reactor", Target, over * RatePerDegree * ColdSubject.ScaleFactor);
                scratch.Add((hull, reactor));
            }
            foreach ((Hull hull, object source) in active) {
                if (!scratch.Contains((hull, source))) { ThermalSourceRegistry.Clear(hull, source); }
            }
            (active, scratch) = (scratch, active);
            scratch.Clear();
        }

        private static float Threshold(Item item) {
            if (!AccountForRods) { return MaxTemperature; }
            int rods = 0;
            if (item.OwnInventory != null) {
                foreach (Item contained in item.OwnInventory.AllItems) { if (contained is { Removed: false }) { rods++; } }
            }
            return RodBase + rods * RodStep;
        }

        private static void Rescan() {
            rescanTimer = 0.0f;
            reactors.Clear();
            List<Item> items = Item.ItemList;
            for (int i = 0; i < items.Count; i++) {
                Item item = items[i];
                if (item.Removed) { continue; }
                Reactor reactor = item.GetComponent<Reactor>();
                if (reactor != null) { reactors.Add(reactor); }
            }
        }
    }
}
