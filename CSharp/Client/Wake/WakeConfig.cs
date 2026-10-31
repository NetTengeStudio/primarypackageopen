using System.Xml.Linq;
using Barotrauma;
using PrimaryPackage.Core;

namespace PrimaryPackage.Wake {
    public static class WakeConfig {
        private const string RelativePath = "Data/pp_wake.xml";

        public static bool EnabledByDefault = false;
        public static float SleepSec = 10.0f;
        public static float DotsSec = 2.0f;
        public static float AlarmHoldSec = 3.0f;
        public static float FadeSec = 1.5f;
        public static float DotsFadeSec = 0.4f;
        public static float DotIntervalSec = 0.5f;
        public static float CharInterval = 0.09f;
        public static float TextScale = 1.0f;
        public static string FontName = "LargeFont";
        public static string TypeSound = "Content/Sounds/UI/pp_type.ogg";
        public static string AlarmSound = "Content/Sounds/Alarms/pp_alarm.ogg";
        public static Identifier[] SkipJobs = System.Array.Empty<Identifier>();
        public static Identifier[] SkipAfflictions = System.Array.Empty<Identifier>();

        private static bool loaded;

        public static void Reset() { loaded = false; }

        public static void EnsureLoaded() {
            if (loaded) { return; }
            loaded = true;
            XElement root = ModPrefs.Root(RelativePath);
            if (root != null) {
                EnabledByDefault = root.GetAttributeBool("enabledbydefault", EnabledByDefault);
                SleepSec = root.GetAttributeFloat("sleepsec", SleepSec);
                DotsSec = root.GetAttributeFloat("dotssec", DotsSec);
                AlarmHoldSec = root.GetAttributeFloat("alarmholdsec", AlarmHoldSec);
                FadeSec = root.GetAttributeFloat("fadesec", FadeSec);
                DotsFadeSec = root.GetAttributeFloat("dotsfadesec", DotsFadeSec);
                DotIntervalSec = root.GetAttributeFloat("dotinterval", DotIntervalSec);
                CharInterval = root.GetAttributeFloat("charinterval", CharInterval);
                TextScale = root.GetAttributeFloat("textscale", TextScale);
                FontName = root.GetAttributeString("font", FontName);
                TypeSound = root.GetAttributeString("typesound", TypeSound);
                AlarmSound = root.GetAttributeString("alarmsound", AlarmSound);
                SkipJobs = Identifiers(root.GetAttributeString("skipjobs", ""));
                SkipAfflictions = Identifiers(root.GetAttributeString("skipafflictions", ""));
            }

            if (SleepSec < 0.1f) { SleepSec = 0.1f; }
            if (DotsSec < 0.1f) { DotsSec = 0.1f; }
            if (AlarmHoldSec < 0.0f) { AlarmHoldSec = 0.0f; }
            if (FadeSec < 0.0f) { FadeSec = 0.0f; }
            if (DotsFadeSec < 0.0f) { DotsFadeSec = 0.0f; }
            if (DotIntervalSec < 0.05f) { DotIntervalSec = 0.05f; }
            if (CharInterval < 0.001f) { CharInterval = 0.001f; }
        }

        private static Identifier[] Identifiers(string list) {
            if (string.IsNullOrWhiteSpace(list)) { return System.Array.Empty<Identifier>(); }
            string[] parts = list.Split(',');
            var result = new System.Collections.Generic.List<Identifier>(parts.Length);
            for (int i = 0; i < parts.Length; i++) {
                string name = parts[i].Trim();
                if (name.Length > 0) { result.Add(name.ToIdentifier()); }
            }
            return result.ToArray();
        }
    }
}
