using System.Collections.Generic;
using Barotrauma;

namespace PrimaryPackage.Spawn {
    public static class CrewSpawnPoints {
        private static readonly HashSet<WayPoint> used = new HashSet<WayPoint>();
        private static readonly List<WayPoint> pool = new List<WayPoint>();

        public static int Replacements { get; private set; }

        public static void Reset() => Replacements = 0;

        public static void CrewPostfix(WayPoint[] __result, Submarine submarine) =>
            Dedup(__result, submarine, outpostOnly: false);

        public static void OutpostPostfix(WayPoint[] __result) =>
            Dedup(__result, Level.Loaded?.StartOutpost, outpostOnly: true);

        private static void Dedup(WayPoint[] result, Submarine submarine, bool outpostOnly) {
            if (result == null || result.Length < 2) { return; }
            used.Clear();
            pool.Clear();

            for (int i = 0; i < result.Length; i++) {
                WayPoint current = result[i];
                if (current == null || used.Add(current)) { continue; }
                WayPoint replacement = TakeFree(result, submarine, outpostOnly);
                if (replacement == null) { continue; }
                result[i] = replacement;
                used.Add(replacement);
                Replacements++;
            }
            used.Clear();
            pool.Clear();
        }

        private static WayPoint TakeFree(WayPoint[] result, Submarine submarine, bool outpostOnly) {
            if (pool.Count == 0) { BuildPool(result, submarine, outpostOnly); }
            for (int i = pool.Count - 1; i >= 0; i--) {
                WayPoint candidate = pool[i];
                pool.RemoveAt(i);
                if (!used.Contains(candidate)) { return candidate; }
            }
            return null;
        }

        private static void BuildPool(WayPoint[] result, Submarine submarine, bool outpostOnly) {
            WayPoint sample = null;
            for (int i = 0; i < result.Length; i++) {
                if (result[i] != null) {
                    sample = result[i];
                    break;
                }
            }
            for (int i = 0; i < WayPoint.WayPointList.Count; i++) {
                WayPoint wp = WayPoint.WayPointList[i];
                if (wp == null || wp.Removed) { continue; }
                if (wp.SpawnType != SpawnType.Human) { continue; }
                if (submarine != null && wp.Submarine != submarine) { continue; }
                if (submarine == null && sample != null && wp.Submarine != sample.Submarine) { continue; }
                if (outpostOnly && sample?.CurrentHull != null && wp.CurrentHull == null) { continue; }
                if (outpostOnly && !SameModuleClass(wp, sample)) { continue; }
                pool.Add(wp);
            }
        }

        private static bool SameModuleClass(WayPoint candidate, WayPoint sample) {
            if (sample?.CurrentHull?.OutpostModuleTags == null) { return true; }
            if (candidate.CurrentHull?.OutpostModuleTags == null) { return false; }
            foreach (Identifier tag in sample.CurrentHull.OutpostModuleTags) {
                foreach (Identifier other in candidate.CurrentHull.OutpostModuleTags) {
                    if (other == tag) { return true; }
                }
            }
            return false;
        }
    }
}
