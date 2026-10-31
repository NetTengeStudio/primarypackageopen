using System;
using HarmonyLib;
using Barotrauma;
namespace PrimaryPackage.CrewManifest {
    [HarmonyPatch(typeof(GameSession), nameof(GameSession.StartRound),
        new Type[] { typeof(LevelData), typeof(bool), typeof(SubmarineInfo), typeof(SubmarineInfo) })]
    internal static class ManifestRoundStartPatch {
        [HarmonyPostfix]
        public static void Postfix() => ManifestState.Clear();
    }
}
