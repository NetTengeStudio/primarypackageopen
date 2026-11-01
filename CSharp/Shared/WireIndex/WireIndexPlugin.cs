using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Netcode;

public sealed class WireIndexPlugin : IModule {
    private readonly PatchSet patches = new PatchSet();
    private Harmony harmony;

    public void Attach(Harmony harmony) {
        this.harmony = harmony;
#if CLIENT
        ClientWireIndex.Attach(harmony, patches);
#else
        ServerWireIndex.Attach(harmony, patches);
#endif
    }

    public void RoundStart() { }

    public void Release() {
        patches.Revert(harmony);
        harmony = null;
        ComponentKey.Clear();
        TypedOrdinal.Clear();
        WireSites.Clear();
#if CLIENT
        EventFrame.Clear();
#endif
    }
}
