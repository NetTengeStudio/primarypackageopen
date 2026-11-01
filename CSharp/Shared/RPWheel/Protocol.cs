#nullable enable

namespace PrimaryPackage.RPWheel
{
    public enum NetHeader : byte
    {
        EmoteRequest    = 1,
        EmoteBroadcast  = 2,
    }

    public static class Cfg
    {
        public const string NetId        = "rpwheel";
        public const double EmoteCooldown = 0.5;

        public const int HeaderMin = 1;
        public const int HeaderMax = 2;

        public const float EmoteRange = Barotrauma.Networking.ChatMessage.SpeakRange;
    }

    public static class RpUtil
    {
        public static void PatchNamespace(HarmonyLib.Harmony harmony)
        {
            foreach (var type in System.Reflection.Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.Namespace != typeof(RpUtil).Namespace) continue;
                if (type.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), true).Length == 0) continue;
                harmony.CreateClassProcessor(type).Patch();
            }
        }
    }
}
