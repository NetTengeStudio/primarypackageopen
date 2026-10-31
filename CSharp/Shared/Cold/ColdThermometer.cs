using System.Linq;
using Barotrauma;
using Barotrauma.Networking;

namespace PrimaryPackage.Cold {
    public static class ColdThermometer {
        public static Identifier ItemId = "pp_thermometer".ToIdentifier();

        public static void Report(Item item, Character user, Character patient) {
            if (item?.Prefab == null || item.Prefab.Identifier != ItemId || user == null || patient == null) { return; }
            Item battery = item.ContainedItems.FirstOrDefault(i => i.HasTag("mobilebattery"));
            if (battery == null || battery.ConditionPercentage <= 0.0f) { return; }

            Hull hull = patient.CurrentHull;
            float temperature = hull != null ? ColdField.GetSmoothed(hull) : ColdConfig.NoHullTemperature;
            string text = TextManager.GetWithVariable("pp_thermometer.reading", "[temp]",
                ((int)System.Math.Round(temperature)).ToString()).Value;
#if CLIENT
            if (user == Character.Controlled) { GUI.AddMessage(text, Microsoft.Xna.Framework.Color.LightCyan, font: GUIStyle.Font); }
#endif
            user.Speak(text, ChatMessageType.Default, minDurationBetweenSimilar: 0.5f);
        }
    }
}
