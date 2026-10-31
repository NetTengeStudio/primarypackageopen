using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;

namespace PrimaryPackage.Cold {
    public static class ColdSuitPatch {
        private const string Marker = "pp_thermalslot";

        public static bool Enabled = true;
        public static Identifier BatteryItems = "mobilebattery".ToIdentifier();
        private static readonly Identifier divingTag = "diving".ToIdentifier();
        private static readonly Identifier exceptionTag = "stasisbag".ToIdentifier();

        public static void Load(XElement root) {
            XElement e = root.Element("SuitSlot");
            if (e == null) { return; }
            Enabled = (bool?)e.Attribute("enabled") ?? Enabled;
            string items = (string)e.Attribute("battery");
            if (!string.IsNullOrWhiteSpace(items)) { BatteryItems = items.Trim().ToIdentifier(); }
        }

        public static void CtorPrefix(Item item, ContentXElement element) {
            if (!Enabled || item?.Prefab == null || element == null) { return; }
            if (element.GetAttributeBool(Marker, false)) { return; }
            if (!item.Prefab.Tags.Contains(divingTag) || item.Prefab.Tags.Contains(exceptionTag)) { return; }
            if (item.Prefab.Identifier == exceptionTag) { return; }

            XElement raw = element.Element;
            raw.SetAttributeValue(Marker, true);
            int capacity = element.GetAttributeInt("capacity", 5);
            raw.SetAttributeValue("capacity", capacity + 1);
            if (element.GetAttributeInt("slotsperrow", 0) is int perRow && perRow > 0) {
                raw.SetAttributeValue("slotsperrow", perRow + 1);
            }
            raw.Add(new XElement("Containable", new XAttribute("items", BatteryItems.Value)));
        }
    }
}
