using System.Collections.Generic;
using Barotrauma;

namespace PrimaryPackage.Thoughts {
    public static class ThoughtPhrases {
        private static readonly string[] Tags = {
            "",
            "pp_thought_idle",
            "pp_thought_fear",
            "pp_thought_irony",
            "pp_thought_aimed",
            "pp_thought_pain",
            "pp_thought_agony",
            "pp_thought_fracture",
            "pp_thought_dislocation",
            "pp_thought_amputation",
            "pp_thought_neardeath",
            "pp_thought_will",
            "pp_thought_wake",
            "pp_thought_daze",
            "pp_thought_broken",
            "pp_thought_hungry",
            "pp_thought_starving",
            "pp_thought_fever",
            "pp_thought_choke",
            "pp_thought_chill",
            "pp_thought_freezing",
            "pp_thought_frostbite",
            "pp_thought_darkness"
        };

        private static readonly string[]?[] table = new string[Tags.Length][];
        private static readonly string[] empty = new string[0];
        private static int languageVersion = -1;

        public static int Count(ThoughtCategory category) => Load(category).Length;

        public static string Get(ThoughtCategory category, int index) {
            string[] list = Load(category);
            return list.Length == 0 ? "" : list[index < 0 || index >= list.Length ? 0 : index];
        }

        public static void Invalidate() {
            for (int i = 0; i < table.Length; i++) { table[i] = null; }
            languageVersion = TextManager.LanguageVersion;
        }

        private static string[] Load(ThoughtCategory category) {
            int index = (int)category;
            if (index <= 0 || index >= Tags.Length) { return empty; }
            if (languageVersion != TextManager.LanguageVersion) { Invalidate(); }
            string[]? cached = table[index];
            if (cached != null) { return cached; }
            var list = new List<string>();
            foreach (string text in TextManager.GetAll(Tags[index])) {
                if (!string.IsNullOrWhiteSpace(text)) { list.Add(text.Trim()); }
            }
            cached = list.Count == 0 ? empty : list.ToArray();
            table[index] = cached;
            return cached;
        }
    }
}
