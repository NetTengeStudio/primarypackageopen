using System;
using HarmonyLib;
using Barotrauma;
using Barotrauma.LuaCs;
namespace PrimaryPackage.CrewManifest {
    public sealed class CrewManifestPlugin : PrimaryPackage.Core.IModule {
        private static readonly Type[] PatchClasses = {
            typeof(IdCardDescriptionPatch),
            typeof(ManifestRoundStartPatch),
#if SERVER
            typeof(SalaryRoundStartPatch),
            typeof(SalaryEnvelopeSpawnPatch),
#endif
#if CLIENT
            typeof(CrewListIconPatch),
            typeof(JobIconDrawPatch),
#endif
        };
        private Harmony harmony;
        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            foreach (Type t in PatchClasses) {
                try { harmony.CreateClassProcessor(t).Patch(); }
                catch (Exception e) { DebugConsole.ThrowError($"crewmanifest не удалось поставить патч {t.Name}", e); }
            }
        }
        public void Release() {
            ManifestState.OnJobChangedLocal = null;
        }
    }
}
