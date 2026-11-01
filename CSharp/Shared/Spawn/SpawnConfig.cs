using System.Xml.Linq;
using Barotrauma;
using PrimaryPackage.Core;

namespace PrimaryPackage.Spawn {
    public static class SpawnConfig {
        private const string RelativePath = "Data/pp_spawn.xml";

        public static Identifier SleepSpawnTag = "pp_sleepspawn".ToIdentifier();
        public static float SleepSearchRadius = 300.0f;
        public static float SeatRetrySeconds = 3.0f;
        public static float WaypointMatchRadius = 4.0f;

        private static bool loaded;

        public static void EnsureLoaded() {
            if (loaded) { return; }
            loaded = true;
            XElement root = ModPrefs.Root(RelativePath);
            if (root == null) { return; }
            SleepSpawnTag = root.GetAttributeIdentifier("sleeptag", SleepSpawnTag);
            SleepSearchRadius = root.GetAttributeFloat("sleepradius", SleepSearchRadius);
            SeatRetrySeconds = root.GetAttributeFloat("seatretryseconds", SeatRetrySeconds);
            WaypointMatchRadius = root.GetAttributeFloat("waypointmatchradius", WaypointMatchRadius);
        }

        public static bool HasSleepTag(WayPoint waypoint) {
            if (waypoint == null) { return false; }
            foreach (Identifier tag in waypoint.Tags) {
                if (tag == SleepSpawnTag) { return true; }
            }
            return false;
        }
    }
}
