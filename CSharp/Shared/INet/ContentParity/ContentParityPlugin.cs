using HarmonyLib;

namespace INet;

public sealed class ContentParityPlugin : PrimaryPackage.Core.IModule {
    public void Attach(Harmony harmony) {
#if CLIENT
        HostLaunch.Attach(harmony);
        LobbyParity.Attach();
#else
        AdvertisedParity.Attach();
#endif
    }

    public void RoundStart() { }

    public void Release() {
#if CLIENT
        LobbyParity.Release();
#endif
    }
}
