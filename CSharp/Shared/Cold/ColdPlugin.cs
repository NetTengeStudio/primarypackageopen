using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace PrimaryPackage.Cold {
    public sealed class ColdPlugin : PrimaryPackage.Core.IModule {
        private const int MaxFailures = 20;

        private Harmony harmony;

        public static bool Simulating =>
            GameMain.GameSession != null &&
            (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient);

        public void RoundStart() => StartRoundPostfix();

        public void Attach(Harmony harmony) {
            this.harmony = harmony;
            ColdConfig.EnsureLoaded();
            harmony.Patch(
                AccessTools.Method(typeof(GameSession), nameof(GameSession.Update)),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(ColdPlugin), nameof(SessionUpdatePostfix)));
            harmony.Patch(
                AccessTools.Method(typeof(Hull), nameof(Hull.Save)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ColdPersistence), nameof(ColdPersistence.SavePostfix))));
            harmony.Patch(
                AccessTools.Method(typeof(Hull), nameof(Hull.Load)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ColdPersistence), nameof(ColdPersistence.LoadPostfix))));
            harmony.Patch(
                AccessTools.Method(typeof(Hull), nameof(Hull.Remove)),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(ColdPlugin), nameof(HullRemovePostfix)));
            harmony.Patch(
                AccessTools.Constructor(typeof(Gap), new[] { typeof(Rectangle), typeof(bool), typeof(Submarine), typeof(bool), typeof(ushort) }),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(ColdPlugin), nameof(HullRemovePostfix)));
            harmony.Patch(
                AccessTools.Method(typeof(Gap), nameof(Gap.Remove)),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(ColdPlugin), nameof(HullRemovePostfix)));
            harmony.Patch(
                AccessTools.Constructor(typeof(ItemContainer), new[] { typeof(Item), typeof(ContentXElement) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ColdSuitPatch), nameof(ColdSuitPatch.CtorPrefix))));
            harmony.Patch(
                AccessTools.Method(typeof(Item), nameof(Item.ApplyTreatment)),
                prefix: PrimaryPackage.Core.ModuleHost.Bind(typeof(ColdPlugin), nameof(ApplyTreatmentPrefix)));
            harmony.Patch(
                AccessTools.Method(typeof(Item), nameof(Item.AddDamage),
                    new[] { typeof(Character), typeof(Vector2), typeof(Attack), typeof(Vector2), typeof(float), typeof(bool) }),
                postfix: PrimaryPackage.Core.ModuleHost.Bind(typeof(ColdPlugin), nameof(AddDamagePostfix)));
            ColdCommands.Register();
        }



        public void Release() {
            ColdCommands.Unregister();
            ColdErcHeat.Reset();
            ColdField.Reset();
        }

        public static void StartRoundPostfix() {
            ColdField.Dirty = true;
            ColdAffliction.Reset();
            ColdIgnite.Reset();
            ColdErcHeat.Reset();
            ThermalSourceRegistry.Reset();
            ColdStages.Reset();
            ColdNeurotraumaBridge.Reset();
            ColdHyperthermia.Reset();
            ColdSuitHeater.Reset();
            ColdFrostbite.Reset();
            ColdReactorHeat.Reset();
            ColdComplications.Reset();
            ColdStasis.Reset();
            ColdSnapshot.Reset();
            failures = 0;
#if SERVER
            ColdServerNet.Reset();
#else
            ColdClientNet.Reset();
#endif
        }

        public static void ApplyTreatmentPrefix(Item __instance, Character user, Character character) {
            ColdTreatment.Inject(__instance);
            if (__instance?.Prefab != null && ColdConfig.WetTreatments.TryGetValue(__instance.Prefab.Identifier, out float dry)) {
                ColdAffliction.ReduceWetness(character, dry);
            }
            ColdThermometer.Report(__instance, user, character);
        }

        public static void AddDamagePostfix(Item __instance, Character attacker) => ColdIgnite.OnItemDamaged(__instance, attacker);

        public static void HullRemovePostfix() => ColdField.Dirty = true;

        private static int failures;

        public static void SessionUpdatePostfix(float deltaTime) {
            if (deltaTime <= 0.0f || failures >= MaxFailures) { return; }
            try {
                ColdSnapshot.Advance();
                if (Simulating) {
                    if (ColdField.Advance(deltaTime)) {
#if SERVER
                        ColdServerNet.Broadcast(ColdField.LastStep);
#endif
                    }
                    ColdSuitHeater.Update(deltaTime);
                    ColdAffliction.Update(deltaTime);
                    ColdHyperthermia.Update(deltaTime);
                    ColdNeurotraumaBridge.Update(deltaTime);
                    ColdFrostbite.Update(deltaTime);
                    ColdComplications.Update(deltaTime);
                    ColdStasis.Update(deltaTime);
                    ColdErcHeat.Update(deltaTime);
                    ColdReactorHeat.Update(deltaTime);
                    ColdIgnite.Update(deltaTime);
                    ThermalSourceRegistry.Update(deltaTime);
                }
#if CLIENT
                ColdField.Interpolate(deltaTime);
                ColdClientNet.Update(deltaTime);
                if (ColdDebug.Enabled) {
                    ColdDebug.UpdateInput();
                    ColdDebugMap.UpdateInput();
                    ColdDebug.UpdateData(deltaTime, Character.Controlled);
                    ColdDebugMap.Update(deltaTime);
                }
#endif
                failures = 0;
            } catch (Exception e) {
                failures++;
                DebugConsole.ThrowError("pp_cold: сбой обновления температуры", e);
                if (failures >= MaxFailures) {
                    DebugConsole.ThrowError("pp_cold: система холода остановлена после " + MaxFailures +
                                            " сбоев подряд. Перезапустите раунд после исправления причины.");
                }
            }
        }
    }
}
