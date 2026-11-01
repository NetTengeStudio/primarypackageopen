using System.Xml.Linq;
using Barotrauma;

namespace PrimaryPackage.Inspect {
    public static class FontMigration {
        public const string DefaultFont = "style:Font";

        public static string Apply(XElement element, string current) {
            if (element == null) { return current; }
            if (element.Attribute("fontname") != null) { return current; }
            XAttribute legacy = element.Attribute("font");
            return legacy == null ? current : FromLegacy(legacy.Value);
        }

        public static string Apply(ContentXElement element, string current) =>
            Apply(element?.Element, current);

        public static string FromLegacy(string value) => value?.Trim().ToLowerInvariant() switch {
            "малый" => "style:SmallFont",
            "заголовок" => "style:SubHeadingFont",
            "крупный" => "style:LargeFont",
            "моноширинный" => "style:MonospacedFont",
            _ => DefaultFont
        };
    }
}
