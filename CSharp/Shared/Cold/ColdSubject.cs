using System.Xml.Linq;
using Barotrauma;

namespace PrimaryPackage.Cold {
    public static class ColdSubject {
        public static bool Disabled;
        public static bool BotsImmune;
        public static float ScaleFactor = 1.0f;

        public static void Load(XElement root) {
            XElement e = root.Element("Characters");
            if (e == null) { return; }
            BotsImmune = (bool?)e.Attribute("botsimmune") ?? BotsImmune;
            ScaleFactor = e.GetAttributeFloat("scalefactor", ScaleFactor);
        }

        public static bool Affects(Character character, AfflictionPrefab prefab, out CharacterHealth health) {
            health = null;
            if (Disabled || character.Removed || character.IsDead || !character.Enabled) { return false; }
            if (BotsImmune && character.IsBot) { return false; }
            if (ColdStasis.Held(character)) { return false; }
            if (prefab != null && !ColdAffliction.IsTarget(prefab, character)) { return false; }
            health = character.CharacterHealth;
            return health != null && !health.Unkillable && !character.GodMode;
        }
    }
}
