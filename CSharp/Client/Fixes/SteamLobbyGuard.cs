using Barotrauma;
using Barotrauma.Networking;
using Barotrauma.Steam;
using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Fixes {
    public sealed class SteamLobbyGuardPlugin : IModule {
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            ModuleHost.Guard("SteamManager.UpdateLobby", () => harmony.Patch(
                AccessTools.Method(typeof(SteamManager), nameof(SteamManager.UpdateLobby), new[] { typeof(ServerSettings) }),
                prefix: ModuleHost.Bind(typeof(SteamLobbyGuardPlugin), nameof(UpdateLobbyPrefix))));
        }

        public void Release() { }

        private static bool warned;

        public static bool UpdateLobbyPrefix() {
            if (SteamManager.IsInitialized) { return true; }
            if (!warned) {
                warned = true;
                ModLog.Warning("steam is unavailable, lobby updates wait for it");
            }
            return false;
        }
    }
}
