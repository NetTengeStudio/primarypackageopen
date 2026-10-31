using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Spawn {
    public sealed class SpawnPlugin : PrimaryPackage.Core.IModule {

        private Harmony harmony;

        public void RoundStart() => StartRoundPostfix();

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            SpawnConfig.EnsureLoaded();

            PrimaryPackage.Core.ModuleHost.Guard("WayPoint.SelectCrewSpawnPoints", () => harmony.Patch(
                AccessTools.Method(typeof(WayPoint), nameof(WayPoint.SelectCrewSpawnPoints)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(CrewSpawnPoints), nameof(CrewSpawnPoints.CrewPostfix)))));

            PrimaryPackage.Core.ModuleHost.Guard("WayPoint.SelectOutpostSpawnPoints", () => harmony.Patch(
                AccessTools.Method(typeof(WayPoint), nameof(WayPoint.SelectOutpostSpawnPoints),
                    new[] { typeof(List<CharacterInfo>), typeof(CharacterTeamType) }),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(CrewSpawnPoints), nameof(CrewSpawnPoints.OutpostPostfix)))));

            PrimaryPackage.Core.ModuleHost.Guard("Character.Create", () => harmony.Patch(
                AccessTools.Method(typeof(Character), nameof(Character.Create),
                    new[] {
                        typeof(CharacterPrefab), typeof(Vector2), typeof(string), typeof(CharacterInfo),
                        typeof(ushort), typeof(bool), typeof(bool), typeof(bool), typeof(RagdollParams), typeof(bool)
                    }),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(SpawnPlugin), nameof(CharacterCreatePostfix))));

            PrimaryPackage.Core.ModuleHost.Guard("GameSession.Update", () => harmony.Patch(
                AccessTools.Method(typeof(GameSession), nameof(GameSession.Update)),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(SpawnPlugin), nameof(SessionUpdatePostfix))));
        }



        public void Release() {
            SpawnPlacement.Reset();
        }

        public static void StartRoundPostfix() {
            SpawnPlacement.Reset();
            CrewSpawnPoints.Reset();
        }

        public static void CharacterCreatePostfix(Character __result, Vector2 position) {
            try {
                SpawnPlacement.OnCharacterCreated(__result, position);
            } catch (Exception e) {
                DebugConsole.ThrowError("pp_spawn: сбой размещения персонажа при спавне", e);
            }
        }

        public static void SessionUpdatePostfix() {
            if (GameMain.NetworkMember is { IsClient: true }) { return; }
            try {
                SpawnPlacement.Update();
            } catch (Exception e) {
                DebugConsole.ThrowError("pp_spawn: сбой досадки персонажа на мебель", e);
            }
        }
    }
}
