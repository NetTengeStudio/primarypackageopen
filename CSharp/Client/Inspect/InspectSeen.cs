using System.Collections.Generic;
using Barotrauma;

namespace PrimaryPackage.Inspect {
    public static class InspectSeen {
        private static readonly HashSet<MapEntity> seen = new HashSet<MapEntity>();

        public static int Count => seen.Count;

        public static bool Blocks(IInspectSettings settings, MapEntity entity) =>
            settings.InspectOnce && seen.Contains(entity);

        public static void Mark(MapEntity entity) {
            if (entity != null && !entity.Removed) { seen.Add(entity); }
        }

        public static void Clear() {
            if (seen.Count > 0) { seen.Clear(); }
        }

        public static void Prune() {
            if (seen.Count > 0) { seen.RemoveWhere(static entity => entity.Removed); }
        }
    }
}
