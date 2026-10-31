using System;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;

namespace PrimaryPackage.Netcode {
    public sealed class EntityIdGuardPlugin : PrimaryPackage.Core.IModule {
        #if CLIENT
        private static int netReadDepth;
#endif

        public void RoundStart() { }

        public void Attach(Harmony harmony) {
            try {
                harmony.Patch(
                    AccessTools.Constructor(typeof(Entity), new[] { typeof(Submarine), typeof(ushort) }),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(EntityIdGuardPlugin), nameof(EntityCtorPrefix))));
            }
            catch (Exception e) {
                DebugConsole.ThrowError("guard Не удалось поставить патч на конструктор Entity защита от конфликтов ID не активна", e);
                return;
            }
#if CLIENT
            try {
                harmony.Patch(
                    AccessTools.Method(typeof(ClientEntityEventManager), "ReadEvent"),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(EntityIdGuardPlugin), nameof(NetReadEnter))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(EntityIdGuardPlugin), nameof(NetReadExit))));
            }
            catch (Exception e) {
                DebugConsole.ThrowError("guard Не удалось поставить патч ReadEvent конфликты при сетевом спавне будут решаться ремапом а не вытеснением", e);
            }
#endif
        }


        public void Release() {
        }

        public static void EntityCtorPrefix(Entity __instance, ref ushort id) {
            if (id == Entity.NullEntityID || id >= Entity.ReservedIDStart) { return; }
            if (__instance is Submarine) { return; }
            Entity existing = Entity.FindEntityByID(id);
            if (existing == null) { return; }
#if CLIENT
            if (netReadDepth > 0 && GameMain.Client != null) {
                Evict(existing);
                if (Entity.FindEntityByID(id) == null) { return; }
            }
#endif
            id = Entity.NullEntityID;
        }

#if CLIENT
        private static void Evict(Entity existing) {
            bool structural = existing is Hull || existing is Gap || existing is Structure
                || existing is WayPoint || existing == Character.Controlled;
            try {
                if (structural) { existing.FreeID(); }
                else { existing.Remove(); }
            }
            catch {
                try { existing.FreeID(); } catch (Exception e) { DebugConsole.ThrowError("pp_idguard - FreeID failed", e); }
            }
        }

        public static void NetReadEnter() { netReadDepth++; }
        public static void NetReadExit() { netReadDepth = Math.Max(0, netReadDepth - 1); }
#endif
    }
}
