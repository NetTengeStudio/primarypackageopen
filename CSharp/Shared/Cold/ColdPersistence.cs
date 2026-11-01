using System.Xml.Linq;
using Barotrauma;

namespace PrimaryPackage.Cold {
    public static class ColdPersistence {
        public const string Attribute = "pp_temp";

        public static void SavePostfix(Hull __instance, XElement __result) {
            if (__result == null || GameMain.GameSession == null) { return; }
            int i = ColdField.IndexOf(__instance);
            if (i < 0) { return; }
            float value = ColdField.Raw(i);
            if (MathUtils.NearlyEqual(value, ColdConfig.BaseTemperature)) { return; }
            __result.Add(new XAttribute(Attribute, value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        }

        public static void LoadPostfix(ContentXElement element, Hull __result) {
            if (__result == null || element == null) { return; }
            XAttribute attribute = element.GetAttribute(Attribute);
            if (attribute == null) { return; }
            ColdField.Restore(__result.ID, element.GetAttributeFloat(Attribute, ColdConfig.BaseTemperature));
            ColdField.Dirty = true;
        }
    }
}
