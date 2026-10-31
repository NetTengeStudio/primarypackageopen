using System;
using System.Reflection;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
namespace PrimaryPackage.Economy {
    public sealed class EconomyRoundResetPlugin : PrimaryPackage.Core.IModule {
        private Harmony harmony;
        public void RoundStart() => StartRoundPostfix();

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
        }
        public void Release() {
        }
        public static void StartRoundPostfix() {
            EconomyClientState.FullSyncRequested = false;
#if SERVER
            OrderRegistry.Clear();
#endif
        }
    }
}
