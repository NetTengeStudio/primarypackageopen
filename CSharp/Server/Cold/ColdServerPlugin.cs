using HarmonyLib;
using PrimaryPackage.Core;

namespace PrimaryPackage.Cold {
    public sealed class ColdServerPlugin : IModule {
        private Harmony harmony;

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            ColdServerNet.Hook();
        }

        public void RoundStart() => ColdServerNet.Reset();

        public void Release() {
            ColdServerNet.Release();
            harmony = null;
        }
    }
}
