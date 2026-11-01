using Barotrauma;

namespace PrimaryPackage.Inspect {
    public sealed class TypewriterCaches {
        public string RawText, ResolvedText;
        public string RawDark, ResolvedDark;
        public string RawTypeSound, ResolvedTypeSound;
    }

    public static class TypewriterText {
        public static string Resolve(string raw) {
            raw ??= "";
            string resolved = TextManager.ContainsTag(raw) ? TextManager.Get(raw).Value : raw;
            return resolved.Replace("\\n", "\n");
        }

        public static string Cached(string raw, ref string rawCache, ref string resolvedCache) {
            raw ??= "";
            if (!ReferenceEquals(raw, rawCache)) {
                rawCache = raw;
                resolvedCache = Resolve(raw);
            }
            return resolvedCache;
        }

        public static string ResolvePath(ContentPackage package, string raw, ref string rawCache, ref string resolvedCache) {
            if (string.IsNullOrWhiteSpace(raw)) { return null; }
            if (!ReferenceEquals(raw, rawCache)) {
                rawCache = raw;
                resolvedCache = ContentPath.FromRaw(package, raw.Trim()).Value;
            }
            return resolvedCache;
        }

        public static string ResolvePath(Item item, string raw, ref string rawCache, ref string resolvedCache) =>
            ResolvePath(item.Prefab.ContentPackage, raw, ref rawCache, ref resolvedCache);
    }
}
